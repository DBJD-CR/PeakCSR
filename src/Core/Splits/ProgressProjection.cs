using System;

namespace PeakCSR
{
    // 进度点的轻量投影 (避免 Core 层反射游戏类型)
    public struct ProgressPointInfo
    {
        public bool HasPosition;
        // 3D 坐标: 平路段与垂直段都要参与归一化
        public float X;
        public float Y;
        public float Z;
        public bool IsNadir;
        // 该点所属群系在本次地图中的序号 (用于按群系统计段数)
        public int Biome;
        // 段名真值
        public string Title;
        // 群系枚举原始值, 用于反查该点属于本次地图的第几个 Segment
        public int BiomeRaw;
    }

    // 路线锚点: 本局必须依次经过的一个真实世界坐标
    //
    // PointIndex >= 0 表示来自某个进度点; -1 表示来自篝火或出生点。
    // IsSummit 表示"到达这里就等于登顶完赛"。
    public struct ProgressAnchor
    {
        public float X;
        public float Y;
        public float Z;
        public int PointIndex;
        public bool IsSummit;
    }

    // 一次采样结果 (含诊断字段)
    public struct ProgressSample
    {
        public bool Valid;
        public float Progress;
        public float Travelled;
        public float Total;
        // 玩家落在第几段 (清洗后主轴链的下标, 1 起)
        public int SegmentIndex;
        // 段内位置 (代价比, 0..1)
        public float SegmentT;
        // 诊断: 本次采样使用的主轴 (AxisZ / AxisY)
        public int Axis;
        // 诊断: 被清洗掉的"沿主轴不推进"锚点数
        public int DroppedAnchors;
    }

    // 进度几何计算，度量用可加的线性代价
    public static class ProgressProjection
    {
        // 垂直位移权重: 每 1 米"向上"海拔按这么多米路程计入
        public const float ClimbWeight = 5f;

        // 下降权重: 下坡也要花时间(控速防摔), 但远小于爬升
        public const float DownWeight = 1f;

        // 沿主轴推进量小于此值的锚点视为"原地重复点", 不单独成段
        // 海岸路线入口篝火压在首个进度点上 (z -363 -> -364) 就是这种点
        public const float MinAxisAdvance = 5f;

        public const int AxisZ = 0;
        public const int AxisY = 1;

        // 复用缓冲区: 本方法每帧会被 UI 多次调用, 避免持续分配
        private const int MaxChain = 64;
        private static readonly ProgressAnchor[] _keptBuf = new ProgressAnchor[MaxChain];
        private static readonly float[] _axisBuf = new float[MaxChain];
        // _cumBuf[i]: 第 i 段起点的累积代价
        private static readonly float[] _cumBuf = new float[MaxChain];
        // _ownBuf[i]: 第 i 段自身的代价 (上一保留点 -> 本保留点)
        private static readonly float[] _ownBuf = new float[MaxChain];

        // 采样进度。纯函数, 不依赖上一帧状态
        public static ProgressSample ComputeByAnchors(ProgressAnchor[] anchors, float px, float py, float pz)
        {
            ProgressSample sample = new ProgressSample();
            if (anchors == null || anchors.Length < 2) return sample;
            if (float.IsNaN(px) || float.IsNaN(py) || float.IsNaN(pz)) return sample;

            // 主轴: 跨度大的那个轴就是推进方向 (仅用于定段与清洗, 不参与代价)
            int axis = ChooseAxis(anchors);
            sample.Axis = axis;
            float playerAxis = axis == AxisZ ? pz : py;

            // 清洗锚点, 得到严格递增的主轴链
            int count;
            int dropped;
            if (!BuildMonotonicChain(anchors, axis, out count, out dropped)) return sample;
            sample.DroppedAnchors = dropped;

            float total = _cumBuf[count - 1] + _ownBuf[count - 1];
            // 锚点几乎全部重合说明链取错了 (例如入口/出口篝火查成同一个)
            if (total <= 1f) return sample;

            // 1) 主轴决定段号
            int seg = LocateSegment(count, playerAxis);

            // 2) 段内用代价比决定走了多少 (与段长同一口径, 天然对齐)
            float t = SegmentCostRatio(seg, py, pz);

            float travelled = _cumBuf[seg] + t * _ownBuf[seg];
            float result = travelled / total;
            // 坐标异常 (NaN) 会让整条计算链产出 NaN, 这里判定为本次采样无效,
            // 交给调用方回退, 而不是把一个 NaN 当成合法进度往上传
            if (float.IsNaN(result) || float.IsInfinity(result)) return sample;
            if (result < 0f) result = 0f;
            if (result > 1f) result = 1f;

            sample.Valid = true;
            sample.Progress = result;
            sample.Travelled = travelled;
            sample.Total = total;
            sample.SegmentIndex = seg;
            sample.SegmentT = t;
            return sample;
        }

        // 主轴 = 跨度更大的那个轴。Z 是常规水平推进方向,
        // 纯竖井/竖直路线 Z 跨度小, 会自动切到 Y 当主轴
        private static int ChooseAxis(ProgressAnchor[] anchors)
        {
            float zMin = float.MaxValue, zMax = float.MinValue;
            float yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < anchors.Length; i++)
            {
                float z = anchors[i].Z;
                float y = anchors[i].Y;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            return (zMax - zMin) >= (yMax - yMin) ? AxisZ : AxisY;
        }

        private static float AxisValue(ProgressAnchor a, int axis)
        {
            return axis == AxisZ ? a.Z : a.Y;
        }

        // 保留首点, 之后只保留"沿主轴推进 >= MinAxisAdvance"的点。
        //
        // 被丢弃锚点只做去重, 不贡献任何代价
        private static bool BuildMonotonicChain(ProgressAnchor[] src, int axis, out int count, out int dropped)
        {
            count = 0;
            dropped = 0;
            if (src == null || src.Length < 2) return false;

            _keptBuf[0] = src[0];
            _axisBuf[0] = AxisValue(src[0], axis);
            _cumBuf[0] = 0f;
            _ownBuf[0] = 0f;
            count = 1;

            for (int k = 1; k < src.Length; k++)
            {
                float here = AxisValue(src[k], axis);

                // 反向点 (here < last) 与原地重复点一样不单独成段
                if (here - _axisBuf[count - 1] < MinAxisAdvance)
                {
                    dropped++;
                    continue;
                }

                if (count >= MaxChain) return false;

                _cumBuf[count] = _cumBuf[count - 1] + _ownBuf[count - 1];
                // 段代价 = 上一保留点到本保留点的直线代价, 与分子口径完全一致
                _ownBuf[count] = StepCost(_keptBuf[count - 1], src[k]);
                _axisBuf[count] = here;
                _keptBuf[count] = src[k];
                count++;
            }

            return count >= 2;
        }

        // 主轴严格递增, 玩家落点唯一确定一段 (只返回段号, 段内位置交给代价比)
        private static int LocateSegment(int count, float playerAxis)
        {
            int last = count - 1;
            if (playerAxis <= _axisBuf[0]) return 1;
            if (playerAxis >= _axisBuf[last]) return last;

            for (int i = 1; i <= last; i++)
            {
                if (playerAxis < _axisBuf[i]) return i;
            }
            return last;
        }

        // 段内位置 = 玩家已付代价 / 本段总代价
        //
        // 分子与分母用同一个 StepCost, 所以:
        //   玩家正好在段起点 -> 0
        //   玩家正好在段终点 -> 1 (与下一段的 0 无缝衔接)
        // 横向偏离不产生任何代价, 不会让进度虚高
        private static float SegmentCostRatio(int seg, float py, float pz)
        {
            float own = _ownBuf[seg];
            if (own <= 0.0001f) return 0f;

            ProgressAnchor a = _keptBuf[seg - 1];
            float paid = StepCost(pz - a.Z, py - a.Y);

            float t = paid / own;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return t;
        }

        // 单腿代价: 水平推进按 1 计, 爬升按 ClimbWeight 计, 下降按 DownWeight 计
        //
        // 只用 Z 作为水平项, 不用 X
        private static float StepCost(float dz, float dy)
        {
            return Math.Abs(dz) + WeightedDy(dy);
        }

        private static float StepCost(ProgressAnchor a, ProgressAnchor b)
        {
            return StepCost(b.Z - a.Z, b.Y - a.Y);
        }

        private static float WeightedDy(float dy)
        {
            return dy >= 0f ? dy * ClimbWeight : -dy * DownWeight;
        }

        // 诊断用: 完整描述真实计算口径 (主轴 / 丢弃点 / 每段 dy dz 与代价占比)
        // 只读, 不参与任何判定
        public static string ChainSummary(ProgressAnchor[] anchors)
        {
            if (anchors == null || anchors.Length < 2) return "(锚点不足)";

            int axis = ChooseAxis(anchors);
            int count;
            int dropped;
            if (!BuildMonotonicChain(anchors, axis, out count, out dropped)) return "(清洗后不足 2 点)";

            float total = _cumBuf[count - 1] + _ownBuf[count - 1];
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("主轴=").Append(axis == AxisZ ? "Z" : "Y");
            sb.Append(" 丢弃=").Append(dropped);
            sb.Append(" 锚点=").Append(count);
            sb.Append(" 总代价=").Append(total.ToString("F0"));
            for (int i = 1; i < count; i++)
            {
                float dy = _keptBuf[i].Y - _keptBuf[i - 1].Y;
                float dz = _keptBuf[i].Z - _keptBuf[i - 1].Z;
                float pct = total > 0.01f ? _ownBuf[i] / total * 100f : 0f;
                sb.Append(" | 段").Append(i)
                  .Append(" dy=").Append(dy.ToString("F0"))
                  .Append(" dz=").Append(dz.ToString("F0"))
                  .Append(" 占").Append(pct.ToString("F0")).Append('%');
            }
            return sb.ToString();
        }

        // 锚点链总代价 (诊断用, 与 ComputeByAnchors 的 total 口径一致)
        public static float TotalLength(ProgressAnchor[] anchors)
        {
            if (anchors == null || anchors.Length < 2) return 0f;
            int count;
            int dropped;
            if (!BuildMonotonicChain(anchors, ChooseAxis(anchors), out count, out dropped)) return 0f;
            return _cumBuf[count - 1] + _ownBuf[count - 1];
        }

        // 未加权 3D 欧氏总长 (诊断用, 用于对比加权前后的量级)
        public static float RawLength(ProgressAnchor[] anchors)
        {
            if (anchors == null || anchors.Length < 2) return 0f;
            float total = 0f;
            for (int k = 1; k < anchors.Length; k++)
            {
                float dx = anchors[k].X - anchors[k - 1].X;
                float dy = anchors[k].Y - anchors[k - 1].Y;
                float dz = anchors[k].Z - anchors[k - 1].Z;
                total += (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return total;
        }

        // 单段代价 (诊断用, 与段长完全一致)
        public static float SegmentLength(ProgressAnchor a, ProgressAnchor b)
        {
            return StepCost(a, b);
        }
    }
}
