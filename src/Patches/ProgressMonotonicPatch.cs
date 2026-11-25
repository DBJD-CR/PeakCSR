using System;
using HarmonyLib;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 修正 RANKED 原生 RaceProgress 的进度跳变与结算失真
    //
    // 几何口径见 ProgressProjection (主轴定段 + 段内加权投影)。
    // 本补丁负责三件事:
    //   1) 用几何值替换 RANKED 的离散索引算法
    //   2) 补回 RANKED 自己的"零点归一化", 但带健全性护栏
    //   3) 单调不减 + 只有确认登顶才给 1.0
    [HarmonyPatch(typeof(RaceProgress), "Current")]
    public static class ProgressMonotonicPatch
    {
        // 开赛首帧的几何值, 作为零点基准
        private static float _geoBaseline;
        private static bool _hasGeoBaseline;
        // 首帧几何异常偏高时放弃基线归零: 宁可显示偏高的真实值, 也不要把整段地图抹平
        private static bool _skipBaselineNormalization;

        // 几何采样连续失效的帧数, 用于诊断"为什么某段时间进度不更新"
        private static int _fallbackFrames;
        private static float _lastFallbackLogAt;
        private const int FallbackWarnFrames = 120;

        public static void ResetGeometryBaseline()
        {
            _geoBaseline = 0f;
            _hasGeoBaseline = false;
            _skipBaselineNormalization = false;
            _fallbackFrames = 0;
            ResetNadirPhase();
        }

        public static void Postfix(ref float __result)
        {
            try
            {
                // 静态闸门: 只在 PCSR 对局内生效, 不污染普通 RANKED 与单人速通
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                if (__result < 0f) return;

                bool finished = HasSelfClimbedToPeak();
                float geometric = ComputeGeometricProgress();
                float value = geometric >= 0f ? geometric : __result;
                TrackFallback(geometric < 0f);

                __result = ProgressMonotonicState.Correct(value, finished);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 进度修正补丁异常: " + ex.Message);
            }
        }

        // 几何链路长时间取不到值时会静默回退到 RANKED 原始值
        private static void TrackFallback(bool failed)
        {
            if (!failed)
            {
                if (_fallbackFrames >= FallbackWarnFrames)
                {
                    PcsrLog.LogInfo("[PeakCSR] 进度几何采样已恢复。");
                }
                _fallbackFrames = 0;
                return;
            }

            _fallbackFrames++;
            if (_fallbackFrames < FallbackWarnFrames) return;
            if (UnityEngine.Time.realtimeSinceStartup - _lastFallbackLogAt < 10f) return;
            _lastFallbackLogAt = UnityEngine.Time.realtimeSinceStartup;

            PcsrLog.LogWarning("[PeakCSR] 进度几何采样连续失效 " + _fallbackFrames
                + " 帧，已回退到 RANKED 原始进度（锚点链或角色坐标不可用）。");
        }

        // 唯一允许把进度强制成 1.0 的条件: 服务端确认"我自己"已经完赛。
        // 不能用 race.Status == finished / Session.State == Finished:
        // 掉线作废、对手先登顶、AutoEndMs 超时收尾都会满足那两个条件,
        // 那些场景下我的进度点可能一个都没点, 却会被显示成 100%。
        private static bool HasSelfClimbedToPeak()
        {
            try
            {
                RaceService svc = RaceService.Instance;
                if (svc == null) return false;
                RaceParticipant self = svc.Self;
                if (self == null) return false;
                return self.State == "finished";
            }
            catch
            {
                return false;
            }
        }

        // 按沿路线加权弧长算连续进度; 任何前提不满足都返回 -1 让调用方回退到 raw
        private static float ComputeGeometricProgress()
        {
            try
            {
                Character local = Character.localCharacter;
                if (local == null) return -1f;

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                IRankedPeakGateway gateway = coordinator != null ? coordinator.Gateway : null;
                if (gateway == null) return -1f;

                int route = session != null ? session.Route : 0;

                // 锚点链由 Gateway 按路线构建: 入口篝火 + 沿途进度点 + 终点锚点。
                // 终点锚点的选择本身就是路线语义 (火山/雾沼到顶峰, 其余到出口篝火)
                ProgressAnchor[] anchors;
                if (!gateway.TryGetRouteAnchors(route, out anchors)) return -1f;
                if (anchors == null || anchors.Length < 2) return -1f;

                // 阵亡后进度必须停止采样
                // 直接用游戏自己的死亡标志 (Character.data.dead)
                if (IsLocalDead(local)) return -1f;

                UnityEngine.Vector3 center = local.Center;

                // 天底阶段: 主山锚点链与天底不共享坐标, 必须整体接管进度
                float nadir;
                if (TryComputeNadirProgress(gateway, center, out nadir))
                {
                    LogNadirProbe(center, nadir, true);
                    return nadir;
                }

                LogNadirProbe(center, -1f, false);

                ProgressSample sample = ProgressProjection.ComputeByAnchors(
                    anchors, center.x, center.y, center.z);
                if (!sample.Valid) return -1f;

                LogAnchorChain(route, anchors);

                float norm = NormalizeByBaseline(sample.Progress);
                norm = ClampUntilFinish(norm, gateway, route, center);
                LogSegmentChange(sample, center, norm);
                return norm;
            }
            catch
            {
                return -1f;
            }
        }

        // ===== 天底阶段 (天阶 8 等强制登天底的局) =====
        private const float NadirEntryProgress = 0.83f;
        private const float NadirTopProgress = 1.0f;

        // 是否已经进入过天底阶段 (进入后即使 inNadir 抖动也不退回主山口径)
        private static bool _nadirEntered;
        private static bool _nadirRollbackArmed;

        public static void ResetNadirPhase()
        {
            _nadirEntered = false;
            _nadirRollbackArmed = false;
        }

        // 天底阶段进度: 只做 Y 轴爬升比
        private static bool TryComputeNadirProgress(
            IRankedPeakGateway gateway, UnityEngine.Vector3 center, out float progress)
        {
            progress = 0f;
            bool inNadir;
            try { inNadir = MapHandler.Instance != null && MapHandler.Instance.inNadir; }
            catch { inNadir = false; }

            // 没进天底且从未进过: 交给主山口径, 天阶 < 8 的局永远走这条
            if (!inNadir && !_nadirEntered) return false;

            if (inNadir)
            {
                _nadirEntered = true;
                // 首次进入时放行一次回落
                if (!_nadirRollbackArmed)
                {
                    _nadirRollbackArmed = true;
                    ProgressMonotonicState.AllowOneRollback();
                }
            }

            float low;
            float high;
            if (!gateway.TryGetNadirBounds(out low, out high))
            {
                // 边界取不到 (天底物件还没激活) 时停在入口值, 不让进度掉回 0
                progress = NadirEntryProgress;
                return true;
            }

            float span = high - low;
            if (span < 1f)
            {
                progress = NadirEntryProgress;
                return true;
            }

            float t = (center.y - low) / span;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            progress = NadirEntryProgress + (NadirTopProgress - NadirEntryProgress) * t;
            if (progress < NadirEntryProgress) progress = NadirEntryProgress;
            if (progress > 0.999f) progress = 0.999f;
            return true;
        }

        // 本机是否已阵亡
        //
        // 用 Character.data.dead 而不是坐标判定: 死亡标志由游戏本体在 RPCA_Die
        // 广播时置位, 是确定性事实; 而坐标在传送后仍会漂移, 不适合当判据。
        private static bool IsLocalDead(Character local)
        {
            try
            {
                if (local == null || local.data == null) return false;
                return local.data.dead;
            }
            catch
            {
                return false;
            }
        }

        // 未点亮篝火前不给 100%
        //
        // 几何上走到终点区域后进度就会到 1.0, 但此时玩家还没点亮篝火/还没登顶完赛,
        // 中间存在一段"显示 100% 却没结束"的窗口。这里把上限压到 0.99,
        // 真正的 100% 只由 HasSelfClimbedToPeak 触发。
        //
        // 只钳"已达终点"的情形, 不影响正常爬升途中的任何数值。
        private const float PreFinishCap = 0.99f;
        private static float ClampUntilFinish(float norm, IRankedPeakGateway gateway, int route, UnityEngine.Vector3 center)
        {
            if (norm < PreFinishCap) return norm;
            try
            {
                // 只有真的贴近终点才钳制, 避免把"几何分母偏小"导致的虚高也一起放过
                bool nearFinish = false;

                float exitZ;
                if (gateway.TryGetRouteExitCampfireZ(route, out exitZ))
                {
                    // 有终点篝火的路线 (单群系 海岸/雨林/森蕈/雪山/方山): 以篝火为准
                    nearFinish = center.z >= exitZ - 6f;
                }
                else
                {
                    // 全山与火山/雾沼: 终点是顶峰, 用锚点链最后一点判定
                    ProgressAnchor[] anchors;
                    if (gateway.TryGetRouteAnchors(route, out anchors) && anchors != null && anchors.Length > 0)
                    {
                        nearFinish = center.z >= anchors[anchors.Length - 1].Z - 6f;
                    }
                }

                return nearFinish ? PreFinishCap : norm;
            }
            catch
            {
                return norm;
            }
        }

        // 开赛首帧的几何值就是"起点进度", 之后线性搬到 0
        // 与 RANKED 原生 Current() 的 _baseline 语义一致:
        //   num2 = 1 - _baseline; (num - _baseline) / num2
        private const float BaselineSanityLimit = 0.10f;

        private static float NormalizeByBaseline(float geo)
        {
            if (!_hasGeoBaseline)
            {
                _geoBaseline = geo;
                _hasGeoBaseline = true;

                if (geo > BaselineSanityLimit)
                {
                    _skipBaselineNormalization = true;
                    PcsrLog.LogWarning("[PeakCSR] 进度零点异常: 开赛首帧几何=" + geo.ToString("F4")
                        + " 高于健全阈值 " + BaselineSanityLimit.ToString("F2")
                        + ", 已放弃基线归零, 直接使用几何进度。");
                    return geo;
                }

                PcsrLog.LogInfo("[PeakCSR] 进度零点: 开赛首帧几何=" + geo.ToString("F4")
                    + ", 已按该点归零 (出生点偏移不再计入进度)");
                return 0f;
            }

            if (_skipBaselineNormalization) return geo;

            float span = 1f - _geoBaseline;
            if (span <= 0.001f) return geo;

            float norm = (geo - _geoBaseline) / span;
            if (norm < 0f) norm = 0f;
            if (norm > 1f) norm = 1f;
            return norm;
        }

        // 天底诊断
        private static bool _probeInit;
        private static bool _lastProbeInNadir;

        private static void LogNadirProbe(UnityEngine.Vector3 center, float progress, bool nadirActive)
        {
            try
            {
                bool inNadir;
                try { inNadir = MapHandler.Instance != null && MapHandler.Instance.inNadir; }
                catch { inNadir = false; }

                // 只在进/出天底那一刻打, 其余帧直接返回
                if (_probeInit && inNadir == _lastProbeInNadir) return;
                _probeInit = true;
                _lastProbeInNadir = inNadir;

                string bounds = "?";
                try
                {
                    MatchCoordinator mc = MatchCoordinator.Instance;
                    IRankedPeakGateway gw = mc != null ? mc.Gateway : null;
                    float low;
                    float high;
                    if (gw != null && gw.TryGetNadirBounds(out low, out high))
                    {
                        bounds = low.ToString("F0") + ".." + high.ToString("F0")
                            + " span=" + (high - low).ToString("F0");
                    }
                }
                catch { }

                PcsrLog.LogInfo("[PeakCSR] 天底探针: inNadir=" + (inNadir ? "是" : "否")
                    + " 接管=" + (nadirActive ? "是" : "否")
                    + " 进度=" + (progress >= 0f ? (progress * 100f).ToString("F1") + "%" : "-")
                    + " 坐标Y=" + center.y.ToString("F1")
                    + " 边界Y=[" + bounds + "]");
            }
            catch { }
        }

        // 锚点链每张图只打一次, 输出真实计算口径供校准
        private static string _lastChainKey;
        private static void LogAnchorChain(int route, ProgressAnchor[] anchors)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < anchors.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append('(').Append(anchors[i].Z.ToString("F0")).Append(')');
                }
                bool summit = false;
                for (int i = 0; i < anchors.Length; i++)
                {
                    if (anchors[i].IsSummit) { summit = true; break; }
                }
                string key = route + "|" + sb;
                if (key == _lastChainKey) return;
                _lastChainKey = key;

                PcsrLog.LogInfo("[PeakCSR] 进度锚点链: route=" + route
                    + " 到顶峰=" + (summit ? "是" : "否")
                    + " 锚点数=" + anchors.Length
                    + " 总长(3D)=" + ProgressProjection.RawLength(anchors).ToString("F0")
                    + " z链=[" + sb + "]");

                // 真实计算口径: 主轴 / 清洗掉的点 / 每段 dy dz 与加权占比
                PcsrLog.LogInfo("[PeakCSR] 进度主轴链: " + ProgressProjection.ChainSummary(anchors));
            }
            catch
            {
            }
        }

        // 段落切换时才打一行, 用于验证"玩家是否真的随着前进在换段"
        private static int _lastLoggedSegment = int.MinValue;
        private static void LogSegmentChange(ProgressSample sample, UnityEngine.Vector3 center, float norm)
        {
            try
            {
                if (sample.SegmentIndex == _lastLoggedSegment) return;
                _lastLoggedSegment = sample.SegmentIndex;
                PcsrLog.LogInfo("[PeakCSR] 进度进入第 " + sample.SegmentIndex + " 段"
                    + " 段内=" + (sample.SegmentT * 100f).ToString("F0") + "%"
                    + " 累计=" + sample.Travelled.ToString("F0") + "/" + sample.Total.ToString("F0")
                    + " 换算=" + (norm * 100f).ToString("F1") + "%"
                    + " 坐标=(" + center.x.ToString("F0") + "," + center.y.ToString("F0") + "," + center.z.ToString("F0") + ")");
            }
            catch
            {
            }
        }
    }

    // 修掉 RANKED 把自己的进度显示成 "??"
    //
    // RANKED 的 OwnDisplayHidden() 是给 Nadir/Gloom 区域做"进度不可信"遮挡用的,
    // 判定方式是 maxProgressPointReached 指向的进度点标题等于 "GLOOM":
    //     return string.Equals(array[maxProgressPointReached]?.title, "GLOOM", ...);
    // 但雾沼/城塞路线 (Route=4, 标题序列 3=GLOOM, 4=THE CITADEL) 的起点恰好是索引 3,
    // JumpToSegment(3) 直接把 maxProgressPointReached 设成 3,
    // 于是整局自己的进度都被遮挡成 "?? %"; 对手看自己的数据不走这条分支, 所以互相看都正常。
    // PCSR 的进度是几何算出来的, 本来就可信, 对局内一律不允许隐藏。
    [HarmonyPatch(typeof(RaceProgress), "OwnDisplayHidden")]
    public static class OwnProgressVisibilityPatch
    {
        public static void Postfix(ref bool __result)
        {
            try
            {
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                __result = false;
            }
            catch
            {
            }
        }
    }

    // MountainProgressHandler.ProgressPoint 是公有嵌套类型, 这里做一个轻量投影避免反射开销
    internal struct ProgressPointEx
    {
        public bool HasPosition;
        // 平路段与垂直段都要参与归一化, 只留 Z 会让平路几乎没有权重
        public float X;
        public float Y;
        public float Z;
        public bool IsNadir;
        // 群系在本次山体里的出现序号 (0 起, 相邻同名群系视为同一群系)
        public int BiomeOrdinal;
        // 段名真值
        public string Title;
        // 群系枚举原始整数值, 用于反查该点属于本次地图的第几个 Segment
        public int BiomeRaw;

        public static ProgressPointEx[] From(Array points)
        {
            if (points == null) return new ProgressPointEx[0];
            int len = points.Length;
            ProgressPointEx[] arr = new ProgressPointEx[len];

            // 用群系枚举值的"出现顺序"编号: RANKED 已按地图实际群系顺序重排 progressPoints,
            // 所以相同 biome 必然连续出现, 序号可以直接按变化累加。
            int ordinal = -1;
            object lastBiome = null;
            for (int i = 0; i < len; i++)
            {
                object p = points.GetValue(i);
                ProgressPointEx item = new ProgressPointEx();
                if (p != null)
                {
                    MountainProgressHandler.ProgressPoint pt = p as MountainProgressHandler.ProgressPoint;
                    if (pt != null)
                    {
                        item.Title = pt.title ?? "";
                        item.BiomeRaw = (int)pt.biome;
                        item.IsNadir = string.Equals(pt.title, "NADIR", StringComparison.OrdinalIgnoreCase);
                        if (!Equals(pt.biome, lastBiome))
                        {
                            ordinal++;
                            lastBiome = pt.biome;
                        }
                        item.BiomeOrdinal = ordinal < 0 ? 0 : ordinal;
                        if (pt.transform != null)
                        {
                            item.HasPosition = true;
                            item.X = pt.transform.position.x;
                            item.Y = pt.transform.position.y;
                            item.Z = pt.transform.position.z;
                        }
                    }
                    else
                    {
                        item.BiomeOrdinal = ordinal < 0 ? 0 : ordinal;
                    }
                }
                else
                {
                    item.BiomeOrdinal = ordinal < 0 ? 0 : ordinal;
                }
                arr[i] = item;
            }
            return arr;
        }
    }

    // RANKED 在开赛瞬间 (ReleaseWhenStartReached) 会调用 RaceProgress.Reset(),
    // 而我们的单调缓存在 StartNewSession 时才重置, 两者临界点不一致。
    // 这里把两个重置对齐, 避免"开赛前采样"被当成"开赛后基准"。
    [HarmonyPatch(typeof(RaceProgress), "Reset")]
    public static class ProgressResetSyncPatch
    {
        public static void Prefix()
        {
            try
            {
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                ProgressMonotonicState.Reset();
                // 几何零点必须与单调缓存同步重置: 否则上一局的零点会带进新的一局
                ProgressMonotonicPatch.ResetGeometryBaseline();
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 进度基准同步异常: " + ex.Message);
            }
        }
    }
}
