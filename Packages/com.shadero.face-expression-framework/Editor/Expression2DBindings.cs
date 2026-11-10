using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>2D Bindの正規化座標と元Clipです。</summary>
    internal sealed class Expression2DPoint
    {
        internal Expression2DPoint(float x, float y, AnimationClip sourceClip)
        {
            X = x;
            Y = y;
            SourceClip = sourceClip;
        }

        public float X { get; }
        public float Y { get; }
        public AnimationClip SourceClip { get; }
    }

    /// <summary>2つの最終係数でチャンネル固有のAnimation Surfaceを選びます。</summary>
    internal sealed class Expression2DBinding
    {
        internal Expression2DBinding(
            AacFlFloatParameter x,
            AacFlFloatParameter y,
            AnimationChannel channel,
            IReadOnlyList<Expression2DPoint> points)
        {
            X = x;
            Y = y;
            Channel = channel;
            Points = points;
        }

        public AacFlFloatParameter X { get; }
        public AacFlFloatParameter Y { get; }
        public AnimationChannel Channel { get; }
        public IReadOnlyList<Expression2DPoint> Points { get; }
    }

    /// <summary>Freeform Directional 2D Bindの0～1座標を集めます。</summary>
    public sealed class SurfaceDraft
    {
        private readonly AacFlFloatParameter _x;
        private readonly AacFlFloatParameter _y;
        private readonly AnimationChannel _channel;
        private readonly List<Expression2DPoint> _points = new List<Expression2DPoint>();

        internal SurfaceDraft(
            AacFlFloatParameter x,
            AacFlFloatParameter y,
            AnimationChannel channel)
        {
            _x = x;
            _y = y;
            _channel = channel;
        }

        internal AacFlFloatParameter X => _x;
        internal AacFlFloatParameter Y => _y;

        public SurfaceDraft At(float x, float y, AnimationClip sourceClip)
        {
            ValidateCoordinate(x, nameof(x));
            ValidateCoordinate(y, nameof(y));
            if (sourceClip == null) throw new ArgumentNullException(nameof(sourceClip));
            if (_points.Any(point => point.X.Equals(x) && point.Y.Equals(y)))
                throw new InvalidOperationException($"A 2D binding point already exists at ({x}, {y}).");
            _points.Add(new Expression2DPoint(x, y, sourceClip));
            return this;
        }

        internal Expression2DBinding Build()
        {
            if (_points.Count == 0)
                throw new InvalidOperationException("A 2D binding requires at least one point.");
            return new Expression2DBinding(_x, _y, _channel, _points.ToArray());
        }

        private static void ValidateCoordinate(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(
                    parameterName, "A 2D binding coordinate must be in the 0..1 range.");
        }
    }
}
