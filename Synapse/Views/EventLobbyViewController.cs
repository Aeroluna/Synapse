﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using HarmonyLib;
using HMUI;
using IPA.Utilities.Async;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.Extras;
using Synapse.HarmonyPatches;
using Synapse.Managers;
using Synapse.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;
using Random = UnityEngine.Random;

namespace Synapse.Views
{
    // TODO: show something else in song info panel when finished
    ////[HotReload(RelativePathToLayout = @"../Resources/Lobby.bsml")]
    [ViewDefinition("Synapse.Resources.Lobby.bsml")]
    internal class EventLobbyViewController : BSMLAutomaticViewController
    {
        private static readonly string[] _randomHeaders =
        {
            "Upcoming",
            "Coming soon",
            "Next up",
            "As seen on TV",
            "Bringing you",
            "Stay tuned for",
            "In the pipeline",
            "Next on the agenda",
            "Prepare for",
            "Launching soon",
            "Coming your way",
            "Next in our lineup",
            "Brace yourself for",
            "Watch out for",
            "Unveiling",
            "Arriving now"
        };

        [UIComponent("chat")]
        private readonly VerticalLayoutGroup _chatObject = null!;

        [UIComponent("scrollview")]
        private readonly ScrollView _scrollView = null!;

        [UIComponent("textbox")]
        private readonly VerticalLayoutGroup _textObject = null!;

        [UIComponent("image")]
        private readonly ImageView _imageView = null!;

        [UIComponent("header")]
        private readonly ImageView _header = null!;

        [UIComponent("headertext")]
        private readonly TextMeshProUGUI _headerText = null!;

        [UIComponent("songtext")]
        private readonly TextMeshProUGUI _songText = null!;

        [UIComponent("artisttext")]
        private readonly TextMeshProUGUI _authorText = null!;

        [UIObject("spinny")]
        private readonly GameObject _loading = null!;

        [UIObject("loading")]
        private readonly GameObject _loadingGroup = null!;

        [UIComponent("progress")]
        private readonly TextMeshProUGUI _progress = null!;

        [UIObject("songinfo")]
        private readonly GameObject _songInfo = null!;

        [UIObject("countdownobject")]
        private readonly GameObject _countdownObject = null!;

        [UIComponent("countdown")]
        private readonly TextMeshProUGUI _countdown = null!;

        [UIObject("startobject")]
        private readonly GameObject _startObject = null!;

        [UIObject("toend")]
        private readonly GameObject _toEndObject = null!;

        [UIComponent("modal")]
        private readonly ModalView _modal = null!;

        private readonly List<ChatMessage> _messageQueue = new();
        private readonly LinkedList<Tuple<ChatMessage, TextMeshProUGUI>> _messages = new();

        private SiraLog _log = null!;
        private Config _config = null!;
        private MessageManager _messageManager = null!;
        private NetworkManager _networkManager = null!;
        private CountdownManager _countdownManager = null!;
        private MapDownloadingManager _mapDownloadingManager = null!;
        private IInstantiator _instantiator = null!;

        private InputFieldView _input = null!;
        private OkRelay _okRelay = null!;
        private Sprite _placeholderSprite = null!;

        private string? _altCoverUrl;
        private float _angle;
        private IPreviewBeatmapLevel? _preview;
        private bool _hasScore;

        public event Action? StartLevel;

        [UsedImplicitly]
        [UIValue("joinChat")]
        private bool JoinChat
        {
            get => _config.JoinChat ?? false;
            set
            {
                _config.JoinChat = value;
                _ = _networkManager.SendBool(value, ServerOpcode.SetChatter);
            }
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

            if (firstActivation)
            {
                InputFieldView original = Resources.FindObjectsOfTypeAll<InputFieldView>().First(n => n.name == "SearchInputField");
                _input = Instantiate(original, _chatObject.transform);
                _input.name = "EventChatInputField";
                RectTransform rect = (RectTransform)_input.transform;
                rect.anchorMin = new Vector2(0, 0);
                rect.anchorMax = new Vector2(1, 1);
                rect.offsetMin = new Vector2(20, 0);
                rect.offsetMax = new Vector2(-20, -70);
                _input._keyboardPositionOffset = new Vector3(0, 60, 0);
                _input._textLengthLimit = 200;
                _instantiator.InstantiateComponent<KeyboardOpener>(_input.gameObject);
                RectTransform bg = (RectTransform)rect.Find("BG");
                bg.offsetMin = new Vector2(0, -4);
                bg.offsetMax = new Vector2(0, 4);
                Transform placeholderText = rect.Find("PlaceholderText");

                // its in Polyglot and im too lazy to add its reference
                // ReSharper disable once Unity.UnresolvedComponentOrScriptableObject
                Destroy(placeholderText.GetComponent("LocalizedTextMeshProUGUI"));
                placeholderText.GetComponent<CurvedTextMeshPro>().text = "Chat";
                ((RectTransform)placeholderText).offsetMin = new Vector2(4, 0);
                ((RectTransform)rect.Find("Text")).offsetMin = new Vector2(4, -4);
                Destroy(rect.Find("Icon").gameObject);

                _scrollView.gameObject.AddComponent<ScrollViewScrollToEnd>().Construct(_toEndObject);
                _toEndObject.SetActive(false);

                _okRelay = _input.gameObject.AddComponent<OkRelay>();
                _okRelay.OkPressed += OnOkPressed;

                _input.gameObject.AddComponent<LayoutElement>().minHeight = 10;
                _imageView.material = Resources.FindObjectsOfTypeAll<Material>().First(n => n.name == "UINoGlowRoundEdge");
                _placeholderSprite = _imageView.sprite;

                _header.color0 = new Color(1, 1, 1, 1);
                _header.color1 = new Color(1, 1, 1, 0);

                _songText.enableAutoSizing = true;
                _authorText.enableAutoSizing = true;
                _songText.fontSizeMin = _songText.fontSize / 2;
                _songText.fontSizeMax = _songText.fontSize;
                _authorText.fontSizeMin = _authorText.fontSize / 2;
                _authorText.fontSizeMax = _authorText.fontSize;

                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_songInfo.transform);

                _startObject.SetActive(false);
            }

            if (addedToHierarchy)
            {
                _messageManager.MessageRecieved += OnMessageRecieved;
                _messageManager.RefreshMotd();
                _networkManager.UserBanned += OnUserBanned;
                ResetLoading();
                _networkManager.MapUpdated += OnMapUpdated;
                _networkManager.HasScoreUpdated += OnHasScoreUpdate;
                OnHasScoreUpdate(_networkManager.Status.HasScore);
                _mapDownloadingManager.MapDownloaded += OnMapDownloaded;
                _mapDownloadingManager.ProgressUpdated += OnProgressUpdated;
                _countdownManager.CountdownUpdated += OnCountdownUpdated;
                _countdownManager.Refresh();
            }

            _headerText.text = _randomHeaders[Random.Range(0, _randomHeaders.Length - 1)] + "...";
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);

            // ReSharper disable once InvertIf
            if (removedFromHierarchy)
            {
                _messageQueue.Clear();
                _messages.Clear();
                foreach (Transform obj in _textObject.transform)
                {
                    Destroy(obj.gameObject);
                }

                _messageManager.MessageRecieved -= OnMessageRecieved;
                _networkManager.UserBanned -= OnUserBanned;
                _networkManager.MapUpdated -= OnMapUpdated;
                _networkManager.HasScoreUpdated -= OnHasScoreUpdate;
                _mapDownloadingManager.MapDownloaded -= OnMapDownloaded;
                _mapDownloadingManager.ProgressUpdated -= OnProgressUpdated;
                _mapDownloadingManager.Cancel();
                _countdownManager.CountdownUpdated -= OnCountdownUpdated;
            }
        }

        [UsedImplicitly]
        [Inject]
        private void Construct(
            SiraLog log,
            Config config,
            MessageManager messageManager,
            NetworkManager networkManager,
            MapDownloadingManager mapDownloadingManager,
            CountdownManager countdownManager,
            IInstantiator instantiator)
        {
            _log = log;
            _config = config;
            _messageManager = messageManager;
            _networkManager = networkManager;
            _mapDownloadingManager = mapDownloadingManager;
            _countdownManager = countdownManager;
            _instantiator = instantiator;
        }

        private void OnOkPressed()
        {
            string text = _input.text;
            _input.ClearInput();
            _messageManager.SendMessage(text);
        }

        private void Update()
        {
            if (_loadingGroup.activeInHierarchy)
            {
                _angle += Time.deltaTime * 200;
                _loading.transform.localEulerAngles = new Vector3(0, 0, _angle);
            }

            if (_messageQueue.Count == 0)
            {
                return;
            }

            try
            {
                float end = _scrollView.contentSize - _scrollView.scrollPageSize;
                bool scrollToEnd = (end < 0) ||
                    (Mathf.Abs(end - _scrollView._destinationPos) < 0.01f);
                float heightLost = 0;

                ChatMessage[] queue = _messageQueue.ToArray();
                _messageQueue.Clear();
                foreach (ChatMessage message in queue)
                {
                    string content;
                    bool rich;
                    if (string.IsNullOrEmpty(message.Id))
                    {
                        content = message.Message;
                        rich = true;
                    }
                    else
                    {
                        content = $"[{message.Username}] {message.Message}";
                        rich = false;
                    }

                    if (_messages.Count > 100)
                    {
                        LinkedListNode<Tuple<ChatMessage, TextMeshProUGUI>> first = _messages.First;
                        _messages.RemoveFirst();
                        TextMeshProUGUI text = first.Value.Item2;
                        float height = text.rectTransform.rect.height;
                        text.richText = rich;
                        text.text = content;
                        text.transform.SetAsLastSibling();
                        first.Value = new Tuple<ChatMessage, TextMeshProUGUI>(message, text);
                        _messages.AddLast(first);
                        heightLost += height;
                    }
                    else
                    {
                        TextMeshProUGUI text =
                            BeatSaberMarkupLanguage.BeatSaberUI.CreateText(
                                (RectTransform)_textObject.transform,
                                content,
                                Vector2.zero);
                        text.enableWordWrapping = true;
                        text.richText = rich;
                        text.alignment = TextAlignmentOptions.Left;
                        text.fontSize = 4;
                        _messages.AddLast(new Tuple<ChatMessage, TextMeshProUGUI>(message, text));
                    }
                }

                Canvas.ForceUpdateCanvases();
                RectTransform contentTransform = _scrollView._contentRectTransform;
                _scrollView.SetContentSize(contentTransform.rect.height);
                float heightDiff = heightLost;
                if (scrollToEnd)
                {
                    heightDiff = _scrollView.contentSize - _scrollView.scrollPageSize - _scrollView._destinationPos;
                }
                else if (heightLost > 0)
                {
                    heightDiff = -heightLost;
                }
                else
                {
                    return;
                }

                _scrollView._destinationPos = Mathf.Max(_scrollView._destinationPos + heightDiff, 0);
                _scrollView.RefreshButtons();
                float newY = Mathf.Max(contentTransform.anchoredPosition.y + heightDiff, 0);
                contentTransform.anchoredPosition = new Vector2(0, newY);
                _scrollView.UpdateVerticalScrollIndicator(Mathf.Abs(newY));
            }
            catch (Exception e)
            {
                _log.Error(e);
            }
        }

        private void OnMapDownloaded(DownloadedMap map)
        {
            _altCoverUrl = string.IsNullOrWhiteSpace(map.Map.AltCoverUrl) ? null : map.Map.AltCoverUrl;

            _preview = map.PreviewBeatmapLevel;
            _songInfo.SetActive(true);
            _loadingGroup.SetActive(false);
            RefreshSongInfo();
        }

        private void RefreshSongInfo()
        {
            if (_altCoverUrl != null && !_hasScore)
            {
                WebRequestExtensions.RequestSprite(_altCoverUrl, n => _imageView.sprite = n);
                _songText.text = "???";
                _authorText.text = "??? [???]";
            }
            else if (_preview != null)
            {
                _ = SetCoverImage(_preview);
                _songText.text = _preview.songName;
                _authorText.text = $"{_preview.songAuthorName} [{_preview.levelAuthorName}]";
            }
            else
            {
                _imageView.sprite = _placeholderSprite;
                _songText.text = "???";
                _authorText.text = "??? [???]";
            }
        }

        private async Task SetCoverImage(IPreviewBeatmapLevel preview)
        {
            _imageView.sprite = await preview.GetCoverImageAsync(CancellationToken.None);
        }

        private void OnMessageRecieved(ChatMessage message)
        {
            _messageQueue.Add(message);
        }

        private void OnUserBanned(string id)
        {
            _messageQueue.RemoveAll(n => n.Id == id);
            _messages.Where(n => n.Item1.Id == id).Do(n => n.Item2.text = "<deleted>");
        }

        private void OnHasScoreUpdate(bool hasScore)
        {
            _hasScore = hasScore;
            UnityMainThreadTaskScheduler.Factory.StartNew(() =>
            {
                RefreshSongInfo();
                if (hasScore && (_networkManager.Status.Map.Ruleset?.AllowResubmission ?? false))
                {
                    _startObject.SetActive(true);
                    _countdownObject.SetActive(false);
                }
                else
                {
                    _startObject.SetActive(false);
                    _countdownObject.SetActive(true);
                }
            });
        }

        private void OnMapUpdated(int index, Map map)
        {
            UnityMainThreadTaskScheduler.Factory.StartNew(ResetLoading);
        }

        private void ResetLoading()
        {
            _progress.text = "Loading...";
            _songInfo.SetActive(false);
            _loadingGroup.SetActive(true);
            RefreshSongInfo();
        }

        private void OnCountdownUpdated(string text)
        {
            _countdown.text = text;
        }

        private void OnProgressUpdated(string message)
        {
            _progress.text = message;
        }

        [UsedImplicitly]
        [UIAction("show-modal")]
        private void ShowModal()
        {
            _modal.Show(true);
        }

        [UsedImplicitly]
        [UIAction("toend-click")]
        private void OnToEndClick()
        {
            _scrollView.ScrollToEnd(true);
        }

        [UsedImplicitly]
        [UIAction("start-click")]
        private void OnStartClick()
        {
            StartLevel?.Invoke();
        }
    }
}
