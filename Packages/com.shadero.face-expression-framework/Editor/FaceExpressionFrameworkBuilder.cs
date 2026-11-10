using System;

namespace FaceExpressionFramework.Editor
{
    /// <summary>
    /// Smoothした入力、Signal計算、Profile、Gestureの生成をまとめます。
    /// </summary>
    public sealed class FaceExpressionFrameworkBuilder
    {
        private readonly FaceBuildContext _context;
        private readonly ClipChannelSplitter _splitter;
        private bool _built;

        private FaceExpressionFrameworkBuilder(FaceBuildContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Channels = new AnimationChannelRegistry();
            _splitter = new ClipChannelSplitter(context.Aac);
            Smoothing = new SmoothingBuilder(context);
            Signals = new ParameterSignalGraph(context, Smoothing);
            Expressions = new ExpressionProfileRegistry(() => Signals.One, Channels);
            Gestures = new GestureProfileConfiguration(Expressions);
            HumanoidEyes = new HumanoidEyeTracking(context, Channels, Gestures);
            LipSync = new LipSyncConfiguration(context);
        }

        internal SmoothingBuilder Smoothing { get; }
        public ParameterSignalGraph Signals { get; }
        public AnimationChannelRegistry Channels { get; }
        public ExpressionProfileRegistry Expressions { get; }
        public GestureProfileConfiguration Gestures { get; }
        public HumanoidEyeTracking HumanoidEyes { get; }
        public LipSyncConfiguration LipSync { get; }

        public static FaceExpressionFrameworkBuilder Create(FaceBuildContext context) =>
            new FaceExpressionFrameworkBuilder(context);

        public void Build()
        {
            if (_built) throw new InvalidOperationException("This face-expression builder has already been built.");
            _built = true;
            Signals.Build();
            Smoothing.Build(Signals.RootLayerName);
            LipSync.BuildControl();
            // SceneのPropertyを動かすレイヤーはAAPだけを生成するレイヤーより後に配置する。
            // 表情レイヤーが先だと、後続レイヤーが姿勢を重ねてBlendShape値を増幅し得る。
            new GestureProfileCompiler(_context, _splitter, LipSync).Build(Gestures);
            LipSync.BuildVisemes();
        }
    }
}
