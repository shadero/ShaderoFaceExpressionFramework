using System;
using AnimatorAsCode.V1;

namespace FaceExpressionFramework.Editor
{
    /// <summary>呼び出し側が範囲を定義する入力です。</summary>
    public sealed class RawSignal
    {
        private readonly ParameterSignalGraph _graph;
        private readonly ParameterSignal _signal;

        internal RawSignal(ParameterSignalGraph graph, ParameterSignal signal)
        {
            _graph = graph;
            _signal = signal;
        }

        /// <summary>範囲変換や他の計算より前に、この入力をSmoothします。</summary>
        public RawSignal Smooth(float local, float remote) =>
            new RawSignal(_graph, _graph.SmoothInput(_signal, local, remote));

        /// <summary>startを0、endを1へ写し、範囲外をClampします。</summary>
        public UnitSignal ToUnit(float start, float end)
        {
            if (float.IsNaN(start) || float.IsInfinity(start) ||
                float.IsNaN(end) || float.IsInfinity(end) || start == end)
                throw new ArgumentException("A finite, non-zero input range is required.");
            return new UnitSignal(_graph, _graph.MapSignal(
                _signal, _graph.NextSignalName("Unit"), start, 0f, end, 1f));
        }

        /// <summary>0を中心とする軸の負側と正側を公開します。</summary>
        public BipolarSignal ToBipolar(float negativeEnd, float positiveEnd) =>
            new BipolarSignal(ToUnit(0f, negativeEnd), ToUnit(0f, positiveEnd));
    }

    /// <summary>軸の非負な2つの側です。</summary>
    public sealed class BipolarSignal
    {
        internal BipolarSignal(UnitSignal negative, UnitSignal positive)
        {
            Negative = negative;
            Positive = positive;
        }

        public UnitSignal Negative { get; }
        public UnitSignal Positive { get; }
    }

    /// <summary>Animation Bindに使う正規化Signalです。</summary>
    public sealed class UnitSignal
    {
        private readonly ParameterSignalGraph _graph;
        internal ParameterSignal Inner { get; }

        internal UnitSignal(ParameterSignalGraph graph, ParameterSignal signal)
        {
            _graph = graph;
            Inner = signal;
        }

        internal AacFlFloatParameter ResolveParameter() => Inner.Parameter;

        /// <summary>normalを正側、priorityを負側へ配置します。</summary>
        public BipolarSignal Prioritize(UnitSignal priority)
        {
            if (priority == null) throw new ArgumentNullException(nameof(priority));
            var name = _graph.NextSignalName("Priority");
            var axis = _graph.Prioritize(name, Inner, priority.Inner);
            var sides = _graph.Split(
                axis, _graph.NextSignalName("Negative"), _graph.NextSignalName("Positive"));
            return new BipolarSignal(
                new UnitSignal(_graph, sides.Lower),
                new UnitSignal(_graph, sides.Upper));
        }

        public UnitSignal Max(UnitSignal other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            return new UnitSignal(_graph, _graph.Max(
                _graph.NextSignalName("Max"), Inner, other.Inner));
        }

        public UnitSignal SuppressBy(
            UnitSignal condition, float startsAt = 0f, float fullyAt = 1f)
        {
            if (condition == null) throw new ArgumentNullException(nameof(condition));
            return new UnitSignal(_graph, _graph.Branch(
                Inner, _graph.NextSignalName("Suppressed"),
                new[] { new ParameterSuppression(condition.Inner, startsAt, fullyAt) }));
        }

        public UnitSignal SuppressBy(params UnitSignal[] conditions)
        {
            if (conditions == null) throw new ArgumentNullException(nameof(conditions));
            if (conditions.Length == 0)
                throw new ArgumentException("At least one condition is required.", nameof(conditions));
            var rules = new ParameterSuppression[conditions.Length];
            for (var index = 0; index < conditions.Length; index++)
            {
                if (conditions[index] == null)
                    throw new ArgumentException("Conditions cannot contain null.", nameof(conditions));
                rules[index] = new ParameterSuppression(conditions[index].Inner, 0f, 1f);
            }
            return new UnitSignal(_graph, _graph.Branch(
                Inner, _graph.NextSignalName("Suppressed"), rules));
        }

        public UnitSignal OnlyWhenIntEquals(string parameterName, int value)
        {
            return new UnitSignal(_graph, _graph.WhenIntEquals(
                Inner, _graph.NextSignalName("Condition"), parameterName, value));
        }
    }
}
