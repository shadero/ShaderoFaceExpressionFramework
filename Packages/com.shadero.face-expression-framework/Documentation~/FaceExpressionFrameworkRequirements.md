# 顔表情 Animator フレームワーク要件

## 対象範囲

本書は、複数アバターで再利用できる顔表情Animatorフレームワークの要件を定義する。Lapwing固有のParameterマッピング、AnimationClip、表情ルールは含めない。

本フレームワークはAnimatorAsCodeから利用するコードファースト方式とする。アバター固有のParameter集約や条件ロジックは、固定されたInspector UIではなく、ユーザーがAnimatorAsCodeで直接記述してよい。

## ユーザーが定義するもの

- 作成するAnimator Parameter、その名前、集約方法はユーザーが決める。
- VRCFT Parameter名や固定の顔Parameter一覧をフレームワークへハードコードしない。
- ユーザーがAnimatorAsCodeで、複数の入力Parameterから1つの派生Parameterを生成できる。
- 1つの派生Parameterは複数の別Parameterを条件として利用できる。
- 条件はON／OFFだけでなく、連続的な係数として扱える。
- 計算したParameterまたは係数を、どのAnimationClipへBindingするかはユーザーが決める。

条件の例：

```text
EffectiveSmile
= Smile
× Gate(MouthShape)
× Gate(CheekPuff)
```

## Parameter Smooth

- Smooth処理をフレームワーク内に持ち、`dev.logilabo.parameter-smoother`を必須依存にしない。
- 計算結果は`dev.logilabo.parameter-smoother` 0.1.1と同等にする。
- 入力ごとにSmoothの有無とLocal用・Remote用のSmoothnessを明示できる。Smoothは入力の加工前に一度だけ適用し、Bindでは追加しない。
- VRChat組み込みParameterの`IsLocal`を使ってLocal／Remote設定を切り替える。
- Rise／FallやAttack／Releaseを分離する必要はない。
- 内部Parameter名、Suffix、Animator Layer名をParameter Smootherと互換にする必要はない。

## Animationチャンネル

- 1つの元AnimationClipを、独立制御できる複数のチャンネルへ分割できる。
- チャンネル名と意味はユーザーが定義する。`Eyes`、`Brows`、`Mouth`などをフレームワークへハードコードしない。
- Curveの所属先は、ユーザーによる明示指定を基本とする。
- 多数のCurveを選択する拡張手段として、AnimatorAsCodeのPredicateも利用できる。
- BlendShape、Transform、その他Component PropertyのCurveを扱える。
- ビルド時に、各チャンネルへ割り当てられたCurveだけを含む分割Clipを生成する。
- 同じCurveが複数チャンネルへ所属した場合はエラーにする。
- ユーザー定義チャンネルへ割り当てられなかったCurveは`Default`チャンネルへ入れる。
- 未分類Curveが存在することだけを理由としたビルド通知は不要。

## Animation Binding

- 計算済みParameterまたは係数とAnimationClipをBindingできる。
- 同じ元Clipから分割した各チャンネルを独立して制御できる。
- 1つの入力Parameterから複数のClipまたはチャンネルを駆動できる。
- Gesture表情を固定Clipとして上乗せするだけでなく、Gestureに応じてフェイストラッキング用AnimationClip自体を差し替えられる。

## VRChat Gesture統合

- 右手Gestureによる表情変更を扱う。
- Profile選択にはVRChat組み込みParameterの`GestureRight`を自動使用し、ユーザーにParameter名を指定させない。
- `GestureRightWeight`はProfile全体の強度には使わず、Fistなどで必要な場合にユーザー定義Signalの入力として利用できる。
- 必要なAnimator Parameter宣言はフレームワークが生成し、Expression Parametersには追加しない。
- Neutralとの遷移だけでなく、異なるGesture同士が直接切り替わる場合も表情を滑らかに遷移させる。
- すべてのGesture遷移に共通する遷移時間を1つ設定できる。
- Gestureごと、または遷移ペアごとの遷移時間Overrideは不要。
- Gesture表情は大きく顔を変えてよく、Gesture専用のフェイストラッキングAnimationClipを選択できる。
- Gesture表情中もフェイストラッキングを継続する。

期待する外部動作：

```text
Neutral -> Fist     : 滑らかに遷移
Fist -> Victory     : 滑らかに遷移
Victory -> Neutral  : 滑らかに遷移
```

内部実装は、外部動作が同等であれば必ずしもAnimator Stateを使用しなくてよい。

## 再利用性と負荷

- フレームワーク本体を変更せず、複数アバターで再利用できる。
- アバター固有のParameter、計算ロジック、チャンネル定義、Clip、Bindingはユーザーコードまたはアバター固有設定から与える。
- 生成するBlend TreeはVRChatアバター用途で許容できる負荷にする。
- 1つのGestureだけが選択されているときに、すべてのGesture用表情を並列評価しない。
- Gestureの分岐を先に選択してから、そのGesture用表情とフェイストラッキングBindingを評価する構造にする。

## 現段階では要件に含めないもの

- Lapwing固有のマッピングや表情ルール。
- 固定された口、目、眉、頬Parameter一覧。
- ハードコードされた顔パーツのチャンネル分類。
- Parameter Smootherとの内部命名互換。
- 値の上昇時と下降時で異なるSmoothness。
- Gestureごとの遷移時間Override。
- `Default`チャンネルへ入ったCurveの通知。
