using System;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>SignalとClipの再利用可能なBindです。Profile間で同一性を保ちます。</summary>
    public sealed class ClipBind
    {
        /// <summary>チャンネルを省略するとProfileの全チャンネルへClipをBindします。</summary>
        public ClipBind(UnitSignal signal, AnimationClip clip, AnimationChannel channel = null)
        {
            Signal = signal;
            Clip = clip;
            Channel = channel;
        }

        internal UnitSignal Signal { get; }
        internal AnimationClip Clip { get; }
        internal AnimationChannel Channel { get; }
    }

    /// <summary>再利用可能な2D Animation Surfaceです。</summary>
    public sealed class SurfaceBind
    {
        private SurfaceBind(Expression2DBinding binding) => Binding = binding;
        internal Expression2DBinding Binding { get; }

        public static SurfaceBind Create(
            UnitSignal x, UnitSignal y, AnimationChannel channel,
            Action<SurfaceDraft> configure)
        {
            var builder = new SurfaceDraft(
                x.ResolveParameter(), y.ResolveParameter(), channel);
            configure(builder);
            return new SurfaceBind(builder.Build());
        }
    }
}
