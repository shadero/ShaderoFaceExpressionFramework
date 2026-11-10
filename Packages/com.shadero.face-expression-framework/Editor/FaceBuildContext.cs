using System;
using AnimatorAsCode.V1;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>1回の顔表情ビルドで各Compilerが共有する参照です。</summary>
    public sealed class FaceBuildContext
    {
        public FaceBuildContext(AacFlBase aac, AacFlController controller, Transform animatorRoot, string systemName)
        {
            Aac = aac ?? throw new ArgumentNullException(nameof(aac));
            Controller = controller ?? throw new ArgumentNullException(nameof(controller));
            AnimatorRoot = animatorRoot != null ? animatorRoot : throw new ArgumentNullException(nameof(animatorRoot));
            SystemName = string.IsNullOrWhiteSpace(systemName)
                ? throw new ArgumentException("A system name is required.", nameof(systemName))
                : systemName.TrimEnd('/');
        }

        public AacFlBase Aac { get; }
        public AacFlController Controller { get; }
        public Transform AnimatorRoot { get; }
        public string SystemName { get; }

        internal string InternalName(string localName)
        {
            if (string.IsNullOrWhiteSpace(localName))
                throw new ArgumentException("An internal name is required.", nameof(localName));
            return $"{SystemName}/{localName.TrimStart('/')}";
        }
    }
}
