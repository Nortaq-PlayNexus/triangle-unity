using System.Collections.Generic;
using Triangle.Core;
using Triangle.Lucidity;
using Triangle.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Triangle.UI
{
    /// <summary>
    /// The in-game HUD, built entirely in C# (no content assets required).
    /// Shows interaction prompts, optional Lucidity/Recognition meters, subtitles,
    /// and a full-screen transition veil for dream/environment changes.
    ///
    /// The HUD binds to the owning pawn's stat components rather than polling them.
    /// This keeps the bars correct after save/load and avoids retaining a destroyed
    /// player through a delegate.
    ///
    /// Port of ATriangleHUD + UTriangleHUDWidget. Unreal's C++ UMG tree becomes a
    /// programmatically assembled uGUI hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleHUD : MonoBehaviour
    {
        private struct QueuedSubtitle
        {
            public string Text;
            public float Duration;
        }

        private enum DreamTransitionPhase
        {
            None,
            FadingOut,
            Holding,
            FadingIn
        }

        [Header("Accessibility")]
        [Range(0.5f, 3f)] [SerializeField] private float subtitleScale = 1f;
        [Range(0f, 1f)] [SerializeField] private float subtitleOpacity = 1f;
        [SerializeField] private Color subtitleColor = new Color(0.92f, 0.92f, 0.96f, 1f);

        [Header("Subtitles")]
        [Tooltip("Prevents an unbounded burst of events from growing memory forever.")]
        [Range(1, 32)] [SerializeField] private int maxSubtitleQueue = 16;

        private Canvas _canvas;
        private Text _interactionName;
        private Text _interactionAction;
        private Image _lucidityBar;
        private Text _lucidityLabel;
        private Image _recognitionBar;
        private Text _recognitionLabel;
        private Text _subtitleText;
        private Text _pauseText;
        private Image _dreamFadeOverlay;

        private readonly Queue<QueuedSubtitle> _subtitleQueue = new Queue<QueuedSubtitle>();
        private float _subtitleRemaining;
        private bool _subtitleActive;

        private Color _dreamFadeColor = Color.black;
        private float _dreamFadeAlpha;
        private float _dreamFadeOutDuration;
        private float _dreamFadeHoldDuration;
        private float _dreamFadeInDuration;
        private float _dreamTransitionElapsed;
        private DreamTransitionPhase _dreamTransitionPhase = DreamTransitionPhase.None;

        private TrianglePlayer _boundPlayer;

        public bool IsPaused { get; private set; }

        /// <summary>Fired at full opacity, when the screen is fully covered.</summary>
        public event System.Action OnDreamTransitionMidpoint;

        /// <summary>Fired when the veil has finished fading back in.</summary>
        public event System.Action OnDreamTransitionFinished;

        private void Awake()
        {
            BuildHud();
        }

        private void OnDestroy()
        {
            UnbindFromPlayer();
        }

        // --- Construction -----------------------------------------------------------

        private static Font LoadDefaultFont()
        {
            // Unity 2022 replaced the built-in Arial with LegacyRuntime.
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            return font;
        }

        private void BuildHud()
        {
            if (_canvas != null)
            {
                return;
            }

            Font font = LoadDefaultFont();

            var canvasObject = new GameObject("TriangleCanvas");
            canvasObject.transform.SetParent(transform, false);
            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasObject.AddComponent<GraphicRaycaster>();

            if (FindObjectOfType<EventSystem>() == null)
            {
                var eventSystemObject = new GameObject("EventSystem");
                eventSystemObject.transform.SetParent(transform, false);
                eventSystemObject.AddComponent<EventSystem>();
                eventSystemObject.AddComponent<StandaloneInputModule>();
            }

            // Full-screen transition veil. Sits first so everything else draws on top.
            _dreamFadeOverlay = CreateImage("DreamFadeOverlay", canvasObject.transform, Color.black);
            StretchFull(_dreamFadeOverlay.rectTransform);
            _dreamFadeOverlay.raycastTarget = false;
            ApplyDreamFade(0f);

            // Interaction prompt, bottom centre.
            _interactionName = CreateText("InteractionName", canvasObject.transform, font, 26, TextAnchor.MiddleCenter);
            _interactionName.color = new Color(0.9f, 0.9f, 0.92f);
            _interactionName.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _interactionName.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _interactionName.rectTransform.pivot = new Vector2(0.5f, 0f);
            _interactionName.rectTransform.anchoredPosition = new Vector2(0f, 132f);
            _interactionName.rectTransform.sizeDelta = new Vector2(760f, 40f);

            _interactionAction = CreateText("InteractionAction", canvasObject.transform, font, 20, TextAnchor.MiddleCenter);
            _interactionAction.color = new Color(0.75f, 0.78f, 0.82f);
            _interactionAction.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _interactionAction.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _interactionAction.rectTransform.pivot = new Vector2(0.5f, 0f);
            _interactionAction.rectTransform.anchoredPosition = new Vector2(0f, 100f);
            _interactionAction.rectTransform.sizeDelta = new Vector2(760f, 30f);

            // Lucidity meter, bottom left.
            _lucidityLabel = CreateText("LucidityLabel", canvasObject.transform, font, 18, TextAnchor.MiddleLeft);
            _lucidityLabel.color = new Color(0.82f, 0.86f, 0.92f);
            _lucidityLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
            _lucidityLabel.rectTransform.anchorMax = new Vector2(0f, 0f);
            _lucidityLabel.rectTransform.pivot = new Vector2(0f, 0f);
            _lucidityLabel.rectTransform.anchoredPosition = new Vector2(40f, 46f);
            _lucidityLabel.rectTransform.sizeDelta = new Vector2(320f, 26f);
            _lucidityLabel.text = "LUCIDITY";

            _lucidityBar = CreateBar("LucidityBar", canvasObject.transform, new Color(0.55f, 0.78f, 0.92f, 0.85f));
            _lucidityBar.rectTransform.anchorMin = new Vector2(0f, 0f);
            _lucidityBar.rectTransform.anchorMax = new Vector2(0f, 0f);
            _lucidityBar.rectTransform.pivot = new Vector2(0f, 0f);
            _lucidityBar.rectTransform.anchoredPosition = new Vector2(40f, 34f);
            _lucidityBar.rectTransform.sizeDelta = new Vector2(320f, 8f);

            // Recognition meter, bottom right.
            _recognitionLabel = CreateText("RecognitionLabel", canvasObject.transform, font, 18, TextAnchor.MiddleRight);
            _recognitionLabel.color = new Color(0.86f, 0.80f, 0.86f);
            _recognitionLabel.rectTransform.anchorMin = new Vector2(1f, 0f);
            _recognitionLabel.rectTransform.anchorMax = new Vector2(1f, 0f);
            _recognitionLabel.rectTransform.pivot = new Vector2(1f, 0f);
            _recognitionLabel.rectTransform.anchoredPosition = new Vector2(-40f, 46f);
            _recognitionLabel.rectTransform.sizeDelta = new Vector2(320f, 26f);
            _recognitionLabel.text = "RECOGNITION";

            _recognitionBar = CreateBar("RecognitionBar", canvasObject.transform, new Color(0.82f, 0.60f, 0.86f, 0.85f));
            _recognitionBar.rectTransform.anchorMin = new Vector2(1f, 0f);
            _recognitionBar.rectTransform.anchorMax = new Vector2(1f, 0f);
            _recognitionBar.rectTransform.pivot = new Vector2(1f, 0f);
            _recognitionBar.rectTransform.anchoredPosition = new Vector2(-40f, 34f);
            _recognitionBar.rectTransform.sizeDelta = new Vector2(320f, 8f);

            // Subtitle, lower third.
            _subtitleText = CreateText("Subtitle", canvasObject.transform, font, 30, TextAnchor.LowerCenter);
            _subtitleText.color = subtitleColor;
            _subtitleText.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            _subtitleText.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            _subtitleText.rectTransform.pivot = new Vector2(0.5f, 0f);
            _subtitleText.rectTransform.anchoredPosition = new Vector2(0f, 200f);
            _subtitleText.rectTransform.sizeDelta = new Vector2(1500f, 140f);
            _subtitleText.raycastTarget = false;
            ApplySubtitleAppearance();
            SetSubtitleText(string.Empty);

            // Pause, dead centre.
            _pauseText = CreateText("Pause", canvasObject.transform, font, 44, TextAnchor.MiddleCenter);
            _pauseText.color = new Color(0.9f, 0.9f, 0.92f);
            StretchFull(_pauseText.rectTransform);
            _pauseText.text = "PAUSED";
            _pauseText.gameObject.SetActive(false);
        }

        private static Image CreateImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Image image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static Text CreateText(string name, Transform parent, Font font, int size, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Image CreateBar(string name, Transform parent, Color color)
        {
            var background = CreateImage(name + "Background", parent, new Color(0f, 0f, 0f, 0.45f));
            StretchFull(background.rectTransform);

            Image bar = CreateImage(name, background.transform, color);
            bar.type = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillAmount = 0f;
            bar.raycastTarget = false;
            StretchFull(bar.rectTransform);
            return bar;
        }

        private static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        // --- Player binding -----------------------------------------------------------

        public void BindToPlayer(TrianglePlayer player)
        {
            UnbindFromPlayer();

            if (player == null)
            {
                return;
            }

            _boundPlayer = player;
            player.Lucidity.OnLucidityChanged += HandleLucidityChanged;
            player.Lucidity.OnLucidityLevelChanged += HandleLucidityLevelChanged;
            player.Recognition.OnRecognitionChanged += HandleRecognitionChanged;
            player.Recognition.OnRecognitionLevelChanged += HandleRecognitionLevelChanged;

            RefreshLucidityDisplay();
            RefreshRecognitionDisplay();
        }

        public void UnbindFromPlayer()
        {
            if (_boundPlayer == null)
            {
                _boundPlayer = null;
                return;
            }

            _boundPlayer.Lucidity.OnLucidityChanged -= HandleLucidityChanged;
            _boundPlayer.Lucidity.OnLucidityLevelChanged -= HandleLucidityLevelChanged;
            _boundPlayer.Recognition.OnRecognitionChanged -= HandleRecognitionChanged;
            _boundPlayer.Recognition.OnRecognitionLevelChanged -= HandleRecognitionLevelChanged;
            _boundPlayer = null;
        }

        public bool IsBoundToPlayer() => _boundPlayer != null;

        // --- Meters ---------------------------------------------------------------------

        private void HandleLucidityChanged(float value) => RefreshLucidityDisplay();

        private void HandleLucidityLevelChanged(TriangleLucidityLevel level)
        {
            // The meter only appears once the player is noticing their own awareness.
            SetLucidityMeterVisible(level >= TriangleLucidityLevel.Low);
        }

        private void HandleRecognitionChanged(float value) => RefreshRecognitionDisplay();

        private void HandleRecognitionLevelChanged(TriangleRecognitionLevel level)
        {
            // Recognition stays hidden until the player has started to notice a pattern.
            SetRecognitionMeterVisible(level >= TriangleRecognitionLevel.Noticing);
        }

        private void RefreshLucidityDisplay()
        {
            if (_lucidityBar == null || _boundPlayer == null)
            {
                return;
            }

            _lucidityBar.fillAmount = _boundPlayer.Lucidity.GetLucidity() / 100f;
        }

        private void RefreshRecognitionDisplay()
        {
            if (_recognitionBar == null || _boundPlayer == null)
            {
                return;
            }

            _recognitionBar.fillAmount = _boundPlayer.Recognition.GetRecognition() / 100f;
        }

        public void SetLucidityValue(float value01)
        {
            if (_lucidityBar != null)
            {
                _lucidityBar.fillAmount = Mathf.Clamp01(value01);
            }
        }

        public void SetLucidityMeterVisible(bool visible)
        {
            if (_lucidityBar != null)
            {
                _lucidityBar.transform.parent.gameObject.SetActive(visible);
            }

            if (_lucidityLabel != null)
            {
                _lucidityLabel.gameObject.SetActive(visible);
            }
        }

        public void SetRecognitionValue(float value01)
        {
            if (_recognitionBar != null)
            {
                _recognitionBar.fillAmount = Mathf.Clamp01(value01);
            }
        }

        public void SetRecognitionMeterVisible(bool visible)
        {
            if (_recognitionBar != null)
            {
                _recognitionBar.transform.parent.gameObject.SetActive(visible);
            }

            if (_recognitionLabel != null)
            {
                _recognitionLabel.gameObject.SetActive(visible);
            }
        }

        // --- Interaction prompt -----------------------------------------------------------

        public void SetInteractionPrompt(string text, string actionName)
        {
            if (_interactionName != null)
            {
                _interactionName.text = text ?? string.Empty;
            }

            if (_interactionAction != null)
            {
                _interactionAction.text = actionName ?? string.Empty;
            }
        }

        // --- Subtitles ------------------------------------------------------------------------

        /// <summary>Queues a subtitle so closely-spaced story beats are not overwritten.</summary>
        public void PushSubtitle(string text, float duration)
        {
            if (string.IsNullOrEmpty(text) || _subtitleText == null)
            {
                return;
            }

            if (_subtitleQueue.Count >= maxSubtitleQueue)
            {
                // Drop the oldest queued beat rather than growing without bound.
                _subtitleQueue.Dequeue();
            }

            _subtitleQueue.Enqueue(new QueuedSubtitle { Text = text, Duration = Mathf.Max(0.1f, duration) });

            if (!_subtitleActive)
            {
                ShowNextSubtitle();
            }
        }

        private void ShowNextSubtitle()
        {
            if (_subtitleQueue.Count == 0)
            {
                _subtitleActive = false;
                SetSubtitleText(string.Empty);
                return;
            }

            QueuedSubtitle next = _subtitleQueue.Dequeue();
            _subtitleRemaining = next.Duration;
            _subtitleActive = true;
            SetSubtitleText(next.Text);
        }

        private void SetSubtitleText(string text)
        {
            if (_subtitleText == null)
            {
                return;
            }

            _subtitleText.text = text;
            _subtitleText.gameObject.SetActive(!string.IsNullOrEmpty(text));
        }

        public void SetSubtitleScale(float scale)
        {
            subtitleScale = Mathf.Clamp(scale, 0.5f, 3f);
            ApplySubtitleAppearance();
        }

        public void SetSubtitleOpacity(float opacity)
        {
            subtitleOpacity = Mathf.Clamp01(opacity);
            ApplySubtitleAppearance();
        }

        private void ApplySubtitleAppearance()
        {
            if (_subtitleText == null)
            {
                return;
            }

            _subtitleText.fontSize = Mathf.RoundToInt(30f * subtitleScale);
            Color color = subtitleColor;
            color.a = subtitleOpacity;
            _subtitleText.color = color;
        }

        // --- Dream transition veil ------------------------------------------------------------

        /// <summary>
        /// Fades the screen to FadeColor, holds at full opacity, then fades back.
        /// The midpoint event is the integration point for swapping a dream
        /// environment while the screen is fully covered.
        /// </summary>
        public void StartDreamTransition(float fadeOutDuration, float holdDuration, float fadeInDuration, Color fadeColor)
        {
            _dreamFadeColor = fadeColor;
            _dreamFadeOutDuration = Mathf.Max(0.01f, fadeOutDuration);
            _dreamFadeHoldDuration = Mathf.Max(0f, holdDuration);
            _dreamFadeInDuration = Mathf.Max(0.01f, fadeInDuration);
            _dreamTransitionElapsed = 0f;
            _dreamTransitionPhase = DreamTransitionPhase.FadingOut;
        }

        /// <summary>Allows a caller to drive the veil manually.</summary>
        public void SetDreamFadeAlpha(float alpha)
        {
            ApplyDreamFade(Mathf.Clamp01(alpha));
        }

        public void StopDreamTransition()
        {
            _dreamTransitionPhase = DreamTransitionPhase.None;
            _dreamTransitionElapsed = 0f;
            ApplyDreamFade(0f);
        }

        public bool IsDreamTransitionActive() => _dreamTransitionPhase != DreamTransitionPhase.None;

        private void ApplyDreamFade(float alpha)
        {
            _dreamFadeAlpha = Mathf.Clamp01(alpha);
            if (_dreamFadeOverlay != null)
            {
                Color color = _dreamFadeColor;
                color.a = _dreamFadeAlpha;
                _dreamFadeOverlay.color = color;
                _dreamFadeOverlay.gameObject.SetActive(_dreamFadeAlpha > 0.001f);
            }
        }

        // --- Pause --------------------------------------------------------------------------------

        public void SetPauseVisible(bool visible)
        {
            IsPaused = visible;
            if (_pauseText != null)
            {
                _pauseText.gameObject.SetActive(visible);
            }
        }

        // --- Tick ------------------------------------------------------------------------------------

        private void Update()
        {
            float deltaSeconds = Time.deltaTime;

            if (_subtitleActive)
            {
                _subtitleRemaining -= deltaSeconds;
                if (_subtitleRemaining <= 0f)
                {
                    ShowNextSubtitle();
                }
            }

            AdvanceDreamTransition(deltaSeconds);
        }

        private void AdvanceDreamTransition(float deltaSeconds)
        {
            if (_dreamTransitionPhase == DreamTransitionPhase.None)
            {
                return;
            }

            _dreamTransitionElapsed += deltaSeconds;

            switch (_dreamTransitionPhase)
            {
                case DreamTransitionPhase.FadingOut:
                    if (_dreamTransitionElapsed >= _dreamFadeOutDuration)
                    {
                        ApplyDreamFade(1f);
                        _dreamTransitionElapsed -= _dreamFadeOutDuration;
                        _dreamTransitionPhase = _dreamFadeHoldDuration > 0f
                            ? DreamTransitionPhase.Holding
                            : DreamTransitionPhase.FadingIn;
                        OnDreamTransitionMidpoint?.Invoke();
                    }
                    else
                    {
                        ApplyDreamFade(_dreamTransitionElapsed / _dreamFadeOutDuration);
                    }

                    break;

                case DreamTransitionPhase.Holding:
                    if (_dreamTransitionElapsed >= _dreamFadeHoldDuration)
                    {
                        _dreamTransitionElapsed -= _dreamFadeHoldDuration;
                        _dreamTransitionPhase = DreamTransitionPhase.FadingIn;
                    }

                    break;

                case DreamTransitionPhase.FadingIn:
                    if (_dreamTransitionElapsed >= _dreamFadeInDuration)
                    {
                        StopDreamTransition();
                        OnDreamTransitionFinished?.Invoke();
                    }
                    else
                    {
                        ApplyDreamFade(1f - (_dreamTransitionElapsed / _dreamFadeInDuration));
                    }

                    break;
            }
        }
    }
}
