# Face Expression Framework 設計

## 境界

入力値の計算とAnimationへのBindを分ける。アバター固有の入力パス、計算式、Clip、顔部位は呼び出し側が決める。フレームワークはVRCFTのパラメータ名や顔の構造を固定しない。現在の公開関数は [FaceExpressionFrameworkApi.md](FaceExpressionFrameworkApi.md) を参照。

## Signal

`Input(path)` は生の値を表す `RawSignal`、`UnitInput(path)` は0～1の `UnitSignal` を返す。`OverrideInputDefault(path, value)` は入力前にAnimatorの初期値を設定する。入力Smoothの出力も同じ初期値から始める。`RawSignal.Smooth(local, remote)` は入力を加工前に一度だけSmoothし、LocalとRemoteで設定を切り替える。`RawSignal.ToUnit(start, end)` は指定範囲を0～1に写し、逆向きの範囲も扱う。`ToBipolar` は `Positive` と `Negative` を持つ。`UnitSignal` のチェーンで優先・最大値・条件付き抑制を指定する。計算は依存順でAnimatorに生成する。BindではSmoothを追加しない。

最大値の判定と出力は同じ時点の入力を使うため、入力を保存して次のAnimator評価で選択する。この最大値演算には1評価の遅延がある。複数条件による抑制は全条件を同時に保存し、条件の切り替え時に異なる時点の値を混ぜない。

## Animation Bind

`ClipBind` はSignal、Clip、Channelの組を保持する。`SurfaceBind` は2つのSignalと0～1座標上のClip群を保持する。BindはProfileとは独立して作り、`ProfileDraft.Add` でProfileへ加える。Gestureから特定のBindを差し替えたり除外したりするときだけ、そのBindを変数に保持する。

Channelは呼び出し側がCurveの所属を定義する。ClipはChannelごとに分割され、同じCurveが複数Channelに入る定義はエラーにする。

## ProfileとGesture

通常のProfileを1つ作り、Gestureごとの差分を `For(gesture, changes)` に記述する。`BaseClip` はGestureの基準姿勢を指定し、`ReplaceClip` は指定BindのClipだけを変更する。`Without(channel)` はその部位のパラメータ駆動1D・2D Bindを除外し、Gesture基準Clipは維持する。`Without(binding)` は指定Bindを、`WithoutBindingsUsing` は指定Signalを使う1D・2D Bindを除外する。

LipSyncは選択したTracking Channelを発話中にVisemeへ切り替え、Viseme Clipからも選択ChannelのCurveだけを生成する。選択ChannelにViseme Curveがなければビルドエラーとする。`BaseClip` は維持する。Humanoid EyesとModular Avatarは必要なアバターだけが利用する。

## 生成順

`Build()` は入力Smooth、Signal計算、LipSync制御、Gesture別Animation、Visemeの順にAnimatorを配置する。基礎となるAnimator操作にはAnimator As Codeを使う。
