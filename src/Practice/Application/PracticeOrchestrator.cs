using UnityEngine;

namespace PeakCSR.Practice
{
    // 练习模式编排器 (应用层)
    //
    // 完整闭环:
    //   Inactive --StartPractice--> Preparing --地图就绪--> Running
    //   Running --探针命中--> MatchFound --3s--> Switching --回主菜单+重连--> 交棒 MatchCoordinator
    //
    // 关键设计取舍:
    //   1) 全程不创建 MatchSession。练习局永远不会触碰 ConnectivityGuardian / 结算 / 上报。
    //   2) 命中后不自己 join, 而是把邀请码交给 MatchCoordinator, 复用排位那条已经打磨过的
    //      join -> 握手 -> 5 秒倒计时 -> 载图链路, 避免两套开赛逻辑漂移。
    //   3) 交棒失败 (重连超时 / 房间已失效) 一律回到可继续练习的状态, 不做任何惩罚。
    public sealed class PracticeOrchestrator
    {
        // 匹配成功后留给玩家反应的时间
        public const float MatchFoundCountdownSeconds = 3f;

        // 从切换到回到主菜单到接续匹配的最长容忍时间
        private const float SwitchTimeoutSeconds = 40f;

        // 交棒 join 的等待上限: 超时视为失败, 回退继续练习
        private const float HandoffTimeoutSeconds = 25f;

        // 单次练习会话内允许的自动回退次数, 超过则彻底收尾
        private const int MaxHandoffRetries = 3;

        private readonly IPracticeRoomLauncher _launcher;
        private readonly IPracticeLobbyProbe _probe;
        private readonly IPracticeNavigator _navigator;
        private readonly IPracticeNotifier _notifier;

        private readonly PracticeSession _session = new PracticeSession();
        private readonly PracticeMatchWatcher _watcher;

        // 交棒回调: 由 MatchCoordinator 注入。
        // 参数是目标房间邀请码 + 结果回调 (true=已受理并进入对局 / false=加入失败)
        private System.Action<string, System.Action<bool, string>> _handoff;

        // 交棒前重校验是否已发起, 避免同一校验点重复请求大厅
        private bool _validating;

        // 探针正忙时把待校验房号记下来, 下一帧重试
        private string _pendingValidateCode = "";
        private float _nextValidateTryAt;

        // 交棒 join 的等待截止时刻 (HandoffRequested 阶段使用)
        private float _handoffDeadline;

        // 交棒代际
        private int _handoffGeneration;

        // 重连代际
        private int _reconnectGeneration;

        // 本次练习会话已自动回退次数
        private int _handoffRetries;

        // 回退重开时的状态回填: StartPractice/Reset 会清空会话, 详情先寄存在这里
        private string _resumeStatus = "";
        private string _resumeRejectedCode = "";

        // 练习自然结束 (登顶/阵亡/主动停止) 的回调: 由门面注入, 负责恢复在线连接
        private System.Action _ended;

        // 交棒成功回调: 门面需要清掉"练习负责恢复在线"的责任标记
        private System.Action _handoffSucceeded;

        // 最近一次失败原因 (Idle 状态下面板展示用)
        private string _lastError = "";

        public PracticeOrchestrator(
            IPracticeRoomLauncher launcher,
            IPracticeLobbyProbe probe,
            IPracticeNavigator navigator,
            IPracticeNotifier notifier)
        {
            _launcher = launcher;
            _probe = probe;
            _navigator = navigator;
            _notifier = notifier;
            _watcher = new PracticeMatchWatcher(probe);
        }

        // 交棒入口由 MatchCoordinator 注入, 避免应用层反向依赖协调器实现
        // 结果回调必须由协调器在 join 成功/失败时各回报一次, 编排器据此决定收尾还是回退
        public void BindHandoff(System.Action<string, System.Action<bool, string>> handoff)
        {
            _handoff = handoff;
        }

        // 练习结束通知: 交棒成功时不会触发 (那种情况由排位链路接管)
        public void BindEnded(System.Action ended)
        {
            _ended = ended;
        }

        // 交棒成功通知: 门面据此交还在线连接的负责权
        public void BindHandoffSucceeded(System.Action handoffSucceeded)
        {
            _handoffSucceeded = handoffSucceeded;
        }

        private void RaiseEnded()
        {
            System.Action h = _ended;
            if (h == null) return;
            try { h(); }
            catch (System.Exception ex) { PcsrLog.LogWarning("[PeakCSR] 结束回调异常: " + ex.Message); }
        }

        public PracticePhase Phase { get { return _session.Phase; } }
        public PracticeRuleset Ruleset { get { return _session.Ruleset; } }
        public string StatusMessage { get { return _session.StatusMessage; } }
        public float QueueElapsedSeconds { get { return _session.QueueElapsedSeconds; } }
        public string LastError { get { return _lastError; } }

        // 命中候选房的房主名 (HUD 展示用); 无候选时为空串
        public string FoundHostName
        {
            get { return _session.Candidate.IsValid ? (_session.Candidate.HostName ?? "") : ""; }
        }

        public bool IsActive
        {
            get { return _session.Phase != PracticePhase.Inactive; }
        }

        // 练习期间地图内抬头可见的用时文本; 非 Running 阶段返回空串
        public string InGameClockText
        {
            get
            {
                if (_session.Phase != PracticePhase.Running) return "";
                string clock = "";
                try
                {
                    RankedPeak.Timer.RaceTimer timer = RankedPeak.Timer.RaceTimer.Instance;
                    if (timer != null && timer.CurrentElapsedMs > 0)
                    {
                        clock = CsrFormat.FormatClock(timer.CurrentElapsedMs / 1000.0);
                    }
                }
                catch
                {
                }
                if (string.IsNullOrEmpty(clock)) return "";
                return CsrLoc.T("practice.ingame.clock",
                    clock, CsrFormat.FormatElapsed(_session.QueueElapsedSeconds));
            }
        }

        // ===== 公共命令 =====

        public bool StartPractice(PcsrRuleFormat format, int ascent)
        {
            if (_session.Phase != PracticePhase.Inactive) return false;
            _handoffRetries = 0;
            _validating = false;
            if (_launcher == null)
            {
                Fail("练习启动器不可用");
                return false;
            }

            // 单群系冲刺与排位建房保持同一取路口径
            System.Random rng = new System.Random(System.Guid.NewGuid().GetHashCode());
            int roll() { return rng.Next(1, Mathf.Max(2, ResolveSprintBiomeCount())); }

            _session.Reset();
            _session.Ruleset = PracticeRuleset.CreateFor(format, ascent, roll);
            _session.Phase = PracticePhase.Preparing;
            _session.StatusMessage = CsrLoc.T("practice.status.preparing", _session.Ruleset.DisplayName);
            _lastError = "";
            _watcher.Reset();
            Notify();

            bool launched = _launcher.Launch(_session.Ruleset, OnLevelReady, OnLaunchFailed);
            if (!launched)
            {
                Fail(CsrLoc.T("practice.status.launch_rejected"));
                return false;
            }
            return true;
        }

        public void StopPractice()
        {
            if (_session.Phase == PracticePhase.Inactive) return;
            // 交棒窗口内不中断
            if (_session.Phase == PracticePhase.Switching)
            {
                PcsrLog.LogInfo("[PeakCSR] 交棒进行中, 忽略结束练习请求。");
                return;
            }

            PcsrLog.LogInfo("[PeakCSR] 主动结束练习 (" + _session.Phase + ")。");
            _lastError = "";
            EndPracticeCleanly("");
        }

        // ===== 每帧驱动 =====

        public void Tick()
        {
            switch (_session.Phase)
            {
                case PracticePhase.Running:
                    TickRunning();
                    break;
                case PracticePhase.MatchFound:
                    TickMatchFound();
                    break;
                case PracticePhase.Switching:
                    TickSwitching();
                    break;
            }
        }

        private void TickRunning()
        {
            // 计时器已经结束 (登顶或阵亡): 练习局自然收尾, 不再继续轮询以免误触交换
            if (IsLocalRunFinished())
            {
                PcsrLog.LogInfo("[PeakCSR] 练习局已结束, 停止后台匹配探针。");
                EndPracticeCleanly(CsrLoc.T("practice.status.finished"));
                return;
            }

            // 玩家从游戏内自己的 UI 退出练习 (Esc -> 主菜单 / 直接退图) 时,
            // 计时器只会回到 Idle 而不是 Finished, 上面那条永远不命中
            // 这里用"离开对局地图/回到主菜单"作为权威离场信号
            if (IsPlayerLeftPracticeScene())
            {
                PcsrLog.LogInfo("[PeakCSR] 检测到已离开练习地图, 收起练习状态并恢复在线。");
                EndPracticeCleanly(CsrLoc.T("practice.status.finished"));
                return;
            }

            _session.StatusMessage = CsrLoc.T("practice.status.running",
                _session.Ruleset.DisplayName, CsrFormat.FormatElapsed(_session.QueueElapsedSeconds));

            _watcher.Tick(_session.Ruleset, _session.RejectedInviteCode, OnMatchFound, OnProbeError);
        }

        // 统一收尾: 停探针 / 撤所有练习标记 / 回 Idle / 通知门面恢复在线
        private void EndPracticeCleanly(string statusMessage)
        {
            if (_launcher != null) _launcher.Abort();
            PracticeRunFlag.Release();
            PracticeRunGate.Disarm();
            PracticeHandoffGuard.EndSwitch();
            _session.Reset();
            _session.StatusMessage = statusMessage ?? "";
            Notify();
            RaiseEnded();
        }

        // 是否已经不在练习地图里了 (退图 / 回主菜单)
        private bool IsPlayerLeftPracticeScene()
        {
            try
            {
                // 导航端口只知道"是不是主菜单"; 地图场景名要另读, 避免把 Airport 也算成离场
                if (_navigator != null && _navigator.IsInMainMenu()) return true;

                UnityEngine.SceneManagement.Scene active =
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                string name = active.name ?? "";
                if (string.IsNullOrEmpty(name)) return false;
                // 与 RANKED 的对局地图判定同源: 只有 Level_* 才是真的在跑图
                return !name.StartsWith("Level_", System.StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private void TickMatchFound()
        {
            float remain = _session.SecondsUntilSwitch(MatchFoundCountdownSeconds);
            if (remain > 0f)
            {
                _session.StatusMessage = CsrLoc.T("practice.status.match_found_countdown",
                    _session.Candidate.HostName, Mathf.CeilToInt(remain));
                Notify();
                return;
            }

            // 倒计时结束立刻开始回城: 房间存活时间很短,
            // 校验要等到重连完成后 join 前的最后一刻再做, 不能在这里拖时间
            BeginSwitch();
        }

        // 交棒前的最后一刻重校验: 此时已回主菜单且重连完成, 离真正 join 只剩一次网络往返
        // 校验通过就立刻交棒, 校验不过就放弃候选 (房间还在就继续用, 没了就重新找)
        private void ValidateThenHandoff(string inviteCode)
        {
            if (_probe == null)
            {
                BeginHandoff(inviteCode);
                return;
            }

            if (_validating) return;
            // 探针上一轮请求还没回来: 记下来延后重试, 不能当成校验失败
            if (_probe.Busy)
            {
                _pendingValidateCode = inviteCode;
                _nextValidateTryAt = Time.realtimeSinceStartup + 0.15f;
                return;
            }

            _pendingValidateCode = "";
            _validating = true;
            _session.StatusMessage = CsrLoc.T("practice.status.validating");
            Notify();

            _probe.Fetch(_session.Ruleset, _session.RejectedInviteCode,
                delegate (System.Collections.Generic.List<PracticeMatchCandidate> list)
                {
                    _validating = false;
                    if (_session.Phase != PracticePhase.Switching) return;

                    // 缓存里包含目标房号 = 仍在 lobby 可加入; 再顺手取一次最新 HostName
                    bool stillJoinable = false;
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (string.Equals(list[i].InviteCode, inviteCode,
                                    System.StringComparison.OrdinalIgnoreCase))
                            {
                                stillJoinable = true;
                                break;
                            }
                        }
                    }

                    if (stillJoinable)
                    {
                        BeginHandoff(inviteCode);
                        return;
                    }

                    // 房间已消失 (开赛/收房/被别人占满): 直接交棒必然 race_started,
                    // 因此放弃候选并重新开一局练习, 让闭环继续
                    RecoverFromFailedHandoff(CsrLoc.T("practice.status.candidate_stale"));
                },
                delegate (string err)
                {
                    _validating = false;
                    // 校验请求本身失败不代表房间失效: 直接交棒, 由 join 结果兜底
                    PcsrLog.LogInfo("[PeakCSR] 交棒前校验请求失败 (继续交棒): " + err);
                    BeginHandoff(inviteCode);
                });
        }

        private void TickSwitching()
        {
            if (_session.SwitchPhase == SwitchStep.ReturningToMenu)
            {
                // LoadScene 是异步的: 调用当帧场景仍是 Level_*,
                // 必须等真的落到主菜单后, 才允许发起重连。
                if (!_navigator.IsInMainMenu()) return;

                _session.SwitchPhase = SwitchStep.Reconnecting;
                // 候选房间先取出来: 回调里会话可能已被重置, 那时 Candidate 已清空
                string targetCode = _session.Candidate.InviteCode;
                // 本轮重连的代际: 回退重开后旧等待器的成功回调必须失效
                int rgen = ++_reconnectGeneration;
                PcsrLog.LogInfo("[PeakCSR] 已回到主菜单, 开始恢复在线连接。");
                _navigator.ReconnectOnline(
                    delegate { OnOnlineReady(rgen, targetCode); },
                    delegate (string err) { OnReconnectFailed(rgen, err); });
                return;
            }

            // 已发起交棒 join: 等协调器回报结果, 超时按失败回退
            if (_session.SwitchPhase == SwitchStep.HandoffRequested)
            {
                if (Time.realtimeSinceStartup < _handoffDeadline) return;
                PcsrLog.LogWarning("[PeakCSR] 交棒 join 等待超时。");
                RecoverFromFailedHandoff(CsrLoc.T("practice.status.handoff_timeout"));
                return;
            }

            // 只有仍停在 Reconnecting 才继续下面的校验/超时判定
            if (_session.SwitchPhase != SwitchStep.Reconnecting) return;

            // 重连完成后停在 Reconnecting 等待校验: 探针忙时按节奏重试
            if (_pendingValidateCode.Length > 0
                && !_validating
                && Time.realtimeSinceStartup >= _nextValidateTryAt)
            {
                string code = _pendingValidateCode;
                _pendingValidateCode = "";
                ValidateThenHandoff(code);
                // 校验同步路径可能已经发起交棒, 这一帧不能再判总超时
                if (_session.SwitchPhase != SwitchStep.Reconnecting) return;
            }

            if (Time.realtimeSinceStartup - _session.SwitchStartedAt > SwitchTimeoutSeconds)
            {
                PcsrLog.LogWarning("[PeakCSR] 接续排位超时, 回到练习模式。");
                RecoverFromFailedHandoff(CsrLoc.T("practice.status.switch_timeout"));
            }
        }

        // ===== 阶段迁移 =====

        private void OnLevelReady()
        {
            if (_session.Phase != PracticePhase.Preparing) return;
            _session.Phase = PracticePhase.Running;
            _session.StartedAt = Time.realtimeSinceStartup;
            _session.QueueStartedAt = Time.realtimeSinceStartup;
            _session.StatusMessage = CsrLoc.T("practice.status.running",
                _session.Ruleset.DisplayName, CsrFormat.FormatElapsed(0f));

            // 回退重开时把"上次为什么失败"补回给 HUD, 并继续排除同一间残房
            if (_resumeStatus.Length > 0)
            {
                _session.StatusMessage = _resumeStatus;
                _lastError = _resumeStatus;
                _resumeStatus = "";
            }
            if (_resumeRejectedCode.Length > 0)
            {
                _session.RejectedInviteCode = _resumeRejectedCode;
                _resumeRejectedCode = "";
            }
            _watcher.Reset();
            PcsrLog.LogInfo("[PeakCSR] 练习局开始, 赛制=" + _session.Ruleset.DisplayName + ", 后台探针已启动。");
            Notify();
            _notifier.PushChat(CsrLoc.T("practice.chat.started", _session.Ruleset.DisplayName));
        }

        private void OnLaunchFailed(string reason)
        {
            PracticeRunFlag.Release();
            PracticeRunGate.Disarm();
            Fail(CsrLoc.T("practice.status.launch_failed", reason ?? ""));
        }

        private void OnMatchFound(PracticeMatchCandidate candidate)
        {
            if (_session.Phase != PracticePhase.Running) return;
            if (!candidate.IsValid) return;

            _session.Candidate = candidate;
            _session.Phase = PracticePhase.MatchFound;
            _session.MatchFoundAt = Time.realtimeSinceStartup;
            _session.StatusMessage = CsrLoc.T("practice.status.match_found_countdown",
                candidate.HostName, Mathf.CeilToInt(MatchFoundCountdownSeconds));

            PcsrLog.LogInfo("[PeakCSR] 匹配成功 [" + candidate.InviteCode + "], "
                + MatchFoundCountdownSeconds + " 秒后返回主菜单接续排位。");
            Notify();
            _notifier.PushChat(CsrLoc.T("practice.chat.match_found", MatchFoundCountdownSeconds));
        }

        private void BeginSwitch()
        {
            _session.Phase = PracticePhase.Switching;
            // 第一步只发回主菜单; 重连要等场景真的切过去才发起 (见 TickSwitching)
            _session.SwitchPhase = SwitchStep.ReturningToMenu;
            _session.SwitchStartedAt = Time.realtimeSinceStartup;
            _session.StatusMessage = CsrLoc.T("practice.status.switching");
            _validating = false;
            _handoffDeadline = 0f;
            _pendingValidateCode = "";
            Notify();

            // 练习局到此为止: 撤销计时标记, 免得玩家在地图里按到收尾逻辑
            PracticeRunFlag.Release();
            PracticeRunGate.Disarm();

            // 切换窗口内冻结 RANKED 的离开对局世界即退房判定,
            // 否则回 Title 的瞬间会把刚 join 到的排位房退掉
            PracticeHandoffGuard.BeginSwitch();

            _navigator.ReturnToMainMenu();
        }

        private void OnOnlineReady(int gen, string inviteCode)
        {
            if (gen != _reconnectGeneration) return;
            if (_session.Phase != PracticePhase.Switching) return;
            if (string.IsNullOrEmpty(inviteCode))
            {
                PcsrLog.LogWarning("[PeakCSR] 交棒缺少目标房间号。");
                RecoverFromFailedHandoff(CsrLoc.T("practice.status.handoff_failed", "缺少房间号"));
                return;
            }

            PcsrLog.LogInfo("[PeakCSR] 在线已恢复, 开始交棒前最后一刻校验 [" + inviteCode + "]。");
            // 房间寿命很短: 先做最后一次"还在不在 lobby"校验, 通过就立刻 join
            ValidateThenHandoff(inviteCode);
        }

        // 真正把房号交给排位协调器, 并等待它的 join 结果
        private void BeginHandoff(string inviteCode)
        {
            if (_session.Phase != PracticePhase.Switching) return;
            if (_handoff == null)
            {
                RecoverFromFailedHandoff(CsrLoc.T("practice.status.handoff_failed", "排位链路不可用"));
                return;
            }

            // 不在这里 Reset: join 是异步的, 结果没回来之前必须保持 Switching,
            // 否则失败后既没有阶段可回退, 练习 HUD 也会提前消失
            _session.SwitchPhase = SwitchStep.HandoffRequested;
            _handoffDeadline = Time.realtimeSinceStartup + HandoffTimeoutSeconds;
            int gen = ++_handoffGeneration;

            try
            {
                PcsrLog.LogInfo("[PeakCSR] 把控制权交还给排位协调器 [" + inviteCode + "]。");
                _handoff(inviteCode, delegate (bool ok, string err)
                {
                    OnHandoffResult(gen, ok, err);
                });
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 交棒异常: " + ex.Message);
                RecoverFromFailedHandoff(CsrLoc.T("practice.status.handoff_failed", ex.Message));
            }
        }

        // 交棒结果: ok=true 交给排位链路彻底接管; ok=false 回退继续练习
        private void OnHandoffResult(int gen, bool ok, string error)
        {
            // 代际不符 = 本次交棒早已被超时/回退作废, 结果直接丢弃
            if (gen != _handoffGeneration) return;
            if (_session.Phase != PracticePhase.Switching) return;
            if (_session.SwitchPhase != SwitchStep.HandoffRequested) return;

            if (ok)
            {
                PcsrLog.LogInfo("[PeakCSR] 交棒成功, 排位对局已接管。");
                PracticeHandoffGuard.EndSwitch();
                _session.Reset();
                _handoffRetries = 0;
                Notify();
                // 在线连接已由交棒流程恢复, 门面不用再负责
                System.Action h = _handoffSucceeded;
                if (h != null)
                {
                    try { h(); }
                    catch (System.Exception ex) { PcsrLog.LogWarning("[PeakCSR] 交棒成功回调异常: " + ex.Message); }
                }
                return;
            }

            RecoverFromFailedHandoff(CsrLoc.T("practice.status.handoff_failed", error ?? ""));
        }

        // 交棒失败回退: 撤销交棒护栏后重新开一局练习, 让闭环不中断
        private void RecoverFromFailedHandoff(string reason)
        {
            PracticeHandoffGuard.EndSwitch();
            _validating = false;
            _pendingValidateCode = "";
            _handoffDeadline = 0f;
            // 让在飞的 join / 重连等待器回调全部失效
            _handoffGeneration++;
            _reconnectGeneration++;

            if (_handoffRetries >= MaxHandoffRetries)
            {
                PcsrLog.LogWarning("[PeakCSR] 交棒连续失败 " + _handoffRetries + " 次, 停止练习。");
                Fail(reason);
                return;
            }

            _handoffRetries++;
            _lastError = reason ?? "";
            PcsrLog.LogWarning("[PeakCSR] 交棒失败, 自动重新开始练习 (第 " + _handoffRetries + " 次): " + _lastError);

            // 记住失效房号, 免得下一次探针立刻又命中同一间残房
            string stale = _session.Candidate.InviteCode;
            PracticeRuleset ruleset = _session.Ruleset;

            // 先彻底撤销旧练习局的所有标记再重开, 避免两层状态叠加
            if (_launcher != null) _launcher.Abort();
            PracticeRunFlag.Release();
            PracticeRunGate.Disarm();
            _session.Reset();
            _session.RejectedInviteCode = stale ?? "";
            _session.StatusMessage = CsrLoc.T("practice.status.handoff_retrying", _handoffRetries);
            Notify();
            if (_notifier != null) _notifier.PushChat(CsrLoc.T("practice.chat.handoff_retry", _lastError));

            RestartPracticeAfterHandoffFailure(ruleset.Format, ruleset.Ascent);
        }

        // 回退重开: 复用 StartPractice 的完整流程, 但保留已累计的重试计数与排除房号
        private void RestartPracticeAfterHandoffFailure(PcsrRuleFormat format, int ascent)
        {
            int retries = _handoffRetries;
            string rejected = _session.RejectedInviteCode;
            string message = _session.StatusMessage;

            // StartPractice 内部会把重试计数清零并 Reset 会话,
            // 这里先把"回退详情"寄存在 _resumeStatus 里, 由 OnLevelReady 回填
            _resumeStatus = message;
            _resumeRejectedCode = rejected;

            if (!StartPractice(format, ascent))
            {
                _resumeStatus = "";
                _resumeRejectedCode = "";
                Fail(message);
                return;
            }

            _handoffRetries = retries;
            if (_session.RejectedInviteCode.Length == 0) _session.RejectedInviteCode = rejected;
        }

        private void OnReconnectFailed(int gen, string reason)
        {
            // 回退重开练习后, 旧的重连等待器仍会回调到这里; 代际不符直接忽略
            if (gen != _reconnectGeneration) return;
            if (_session.Phase != PracticePhase.Switching) return;
            if (_session.SwitchPhase != SwitchStep.Reconnecting) return;

            PcsrLog.LogWarning("[PeakCSR] 重连失败, 回到练习模式: " + reason);
            // 重连失败也走回退: 练习启动链路会自行切回离线模式, 玩家不必手动重开
            RecoverFromFailedHandoff(CsrLoc.T("practice.status.reconnect_failed", reason ?? ""));
        }

        private void OnProbeError(string err)
        {
            // 探针失败不打断练习, 只记日志; 面板上不刷错误以免干扰
            PcsrLog.LogInfo("[PeakCSR] 大厅探针本轮失败 (不影响练习): " + err);
        }

        private void Fail(string message)
        {
            _lastError = message ?? "";
            _session.Reset();
            _session.StatusMessage = _lastError;
            PcsrLog.LogWarning("[PeakCSR] " + _lastError);
            Notify();
            if (_notifier != null) _notifier.PushChat(CsrLoc.T("practice.chat.error", _lastError));
            RaiseEnded();
        }

        private void Notify()
        {
            if (_notifier != null) _notifier.NotifyStateChanged();
        }

        // ===== 环境探测 =====

        private static bool IsLocalRunFinished()
        {
            try
            {
                RankedPeak.Timer.RaceTimer timer = RankedPeak.Timer.RaceTimer.Instance;
                if (timer == null) return false;
                return timer.State == RankedPeak.Timer.TimerState.Finished;
            }
            catch
            {
                return false;
            }
        }

        private static int ResolveSprintBiomeCount()
        {
            try { return Mathf.Max(2, RankedPeak.Services.RunConfigService.MiniRunBiomeCount + 1); }
            catch { return 5; }
        }
    }
}
