using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.ViewControllers;
using HarmonyLib;
using HMUI;
using IPA.Utilities.Async;
using JetBrains.Annotations;
using SiraUtil.Logging;
using Synapse.Controllers;
using Synapse.HarmonyPatches;
using Synapse.Managers;
using Synapse.Networking.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace Synapse.Views;

[ViewDefinition("Synapse.Resources.LobbyChat.bsml")]
internal class EventLobbyChatViewController : BSMLAutomaticViewController
{
    private static readonly ProfanityFilter.ProfanityFilter _profanityFilter = new();

    [UIComponent("chat")]
    private readonly VerticalLayoutGroup _chatObject = null!;

    [UIComponent("modal")]
    private readonly ModalView _modal = null!;

    [UIComponent("player-count")]
    private readonly TextMeshProUGUI _playerCount = null!;

    [UIComponent("division-setting")]
    private readonly DropDownListSetting _divisionSetting = null!;

    [UIObject("replay-intro-button")]
    private readonly GameObject _replayIntroObject = null!;

    [UIObject("replay-outro-button")]
    private readonly GameObject _replayOutroObject = null!;

    [UIComponent("scrollview")]
    private readonly ScrollView _scrollView = null!;

    [UIComponent("textbox")]
    private readonly VerticalLayoutGroup _textObject = null!;

    [UIObject("toend")]
    private readonly GameObject _toEndObject = null!;

    [UIComponent("priority-bg")]
    private readonly Backgroundable _priorityBg = null!;

    [UIComponent("priority-bg")]
    private readonly VerticalLayoutGroup _priorityVertical = null!;

    private readonly List<ChatMessage> _messageQueue = [];
    private readonly LinkedList<(ChatMessage ChatMessage, TextMeshProUGUI TextMesh)> _messages = [];

    private readonly Stack<PriorityMessage> _disabledPriorityMessages = [];
    private readonly List<PriorityMessage> _priorityMessages = [];

    private SiraLog _log = null!;
    private Config _config = null!;
    private MessageManager _messageManager = null!;
    private NetworkManager _networkManager = null!;
    private ListingManager _listingManager = null!;
    private IInstantiator _instantiator = null!;
    private EventLeaderboardViewController _leaderboardViewController = null!;
    private InputFieldView _input = null!;
    private KeyboardOpener _keyboardOpener = null!;
    private OkRelay _okRelay = null!;

    private string _playerCountText = string.Empty;

    internal event Action? IntroStarted;

    internal event Action? OutroStarted;

    [UsedImplicitly]
    [UIValue("join-chat")]
    private bool JoinChat
    {
        get => _config.JoinChat ?? false;
        set
        {
            _config.JoinChat = value;
            _ = _networkManager.Send(ServerOpcode.SetChatter, value);
        }
    }

    [UsedImplicitly]
    [UIValue("mute-music")]
    private bool DisableLobbyAudio
    {
        get => _config.DisableLobbyAudio;
        set => _config.DisableLobbyAudio = value;
    }

    [UsedImplicitly]
    [UIValue("profanity-filter")]
    private bool ProfanityFilter
    {
        get => _config.ProfanityFilter;
        set => _config.ProfanityFilter = value;
    }

    [UsedImplicitly]
    [UIValue("join-leave-messages")]
    private bool ShowJoinLeaveMessages
    {
        get => _config.ShowJoinLeaveMessages;
        set => _config.ShowJoinLeaveMessages = value;
    }

    [UsedImplicitly]
    [UIValue("division")]
    private int Division
    {
        get => _config.LastEvent.Division ?? 0;
        set
        {
            _config.LastEvent.Division = value;
            _leaderboardViewController.InvalidateAllScores();
            _ = _networkManager.Send(ServerOpcode.SetDivision, value);
        }
    }

    [UsedImplicitly]
    [UIValue("division-choices")]
    private List<object> DivisionChoices { get; set; } = [0];

#if !V1_29_1
    protected override void OnDestroy()
#else
    public override void OnDestroy()
#endif
    {
        base.OnDestroy();
        _networkManager.PlayerCountUpdated -= OnPlayerCountUpdated;
    }

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);

        rectTransform.sizeDelta = new Vector2(-40, 0);

        if (firstActivation)
        {
            InputFieldView original =
                Resources.FindObjectsOfTypeAll<InputFieldView>().First(n => n.name == "SearchInputField");
            _input = Instantiate(original, _chatObject.transform);
            _input.name = "EventChatInputField";
            RectTransform rect = (RectTransform)_input.transform;
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = new Vector2(20, 0);
            rect.offsetMax = new Vector2(-20, -70);
            _input._keyboardPositionOffset = new Vector3(0, 60, 0);
            _input._textLengthLimit = 200;
            _input._textView.richText = false;
            _keyboardOpener = _instantiator.InstantiateComponent<KeyboardOpener>(_input.gameObject);
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

#if PRE_V1_40_8
            ImageView priorityBg = (ImageView)_priorityBg.background;
#else
            ImageView priorityBg = (ImageView)_priorityBg.Background;
#endif
            priorityBg._skew = 0;
            priorityBg._gradientDirection = ImageView.GradientDirection.Vertical;
            priorityBg.gradient = true;
            priorityBg.color = Color.white;
            priorityBg.color0 = new Color(0.13f, 0.01f, 0.09f);
            priorityBg.color1 = new Color(0.09f, 0.01f, 0.13f);
            ((RectTransform)_priorityVertical.transform).pivot = new Vector2(0.5f, 1);
            _priorityVertical.gameObject.SetActive(false);
        }

        // ReSharper disable once InvertIf
        if (addedToHierarchy)
        {
            _messageManager.MessageReceived += OnMessageReceived;
            _messageManager.RefreshMotd();
            _networkManager.UserBanned += OnUserBanned;
            OnStageUpdated(_networkManager.Status.Stage);
            _networkManager.StageUpdated += OnStageUpdated;

            Listing? listing = _listingManager.Listing;
            GameObject parent = _divisionSetting.transform.parent.gameObject;
            if (listing != null)
            {
                parent.SetActive(listing.Divisions.Count > 0);
                DivisionChoices = Enumerable.Range(0, Math.Max(listing.Divisions.Count, 1)).Cast<object>().ToList();
            }
            else
            {
                parent.SetActive(false);
                DivisionChoices = [0];
            }

#if !PRE_V1_40_8
            _divisionSetting.Values = DivisionChoices;
#else
            _divisionSetting.values = DivisionChoices;
#endif
            _divisionSetting.UpdateChoices();
            _divisionSetting.Value = Division;
        }
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);

        // ReSharper disable once InvertIf
        if (removedFromHierarchy)
        {
            _priorityVertical.gameObject.SetActive(false);
            _priorityMessages.Clear();
            _disabledPriorityMessages.Clear();
            foreach (Transform obj in _priorityVertical.transform)
            {
                Destroy(obj.gameObject);
            }

            _messageQueue.Clear();
            _messages.Clear();
            foreach (Transform obj in _textObject.transform)
            {
                Destroy(obj.gameObject);
            }

            _messageManager.MessageReceived -= OnMessageReceived;
            _networkManager.UserBanned -= OnUserBanned;
            _networkManager.StageUpdated -= OnStageUpdated;
        }

        _keyboardOpener.Close();
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(
        SiraLog log,
        Config config,
        MessageManager messageManager,
        NetworkManager networkManager,
        ListingManager listingManager,
        IInstantiator instantiator,
        EventLeaderboardViewController leaderboardViewController)
    {
        _log = log;
        _config = config;
        _messageManager = messageManager;
        _networkManager = networkManager;
        _listingManager = listingManager;
        _instantiator = instantiator;
        _leaderboardViewController = leaderboardViewController;
        networkManager.PlayerCountUpdated += OnPlayerCountUpdated;
    }

    private void OnMessageReceived(ChatMessage message)
    {
        _messageQueue.Add(message);
    }

    private void OnOkPressed()
    {
        string text = _input.text;
        _input.ClearInput();
        _messageManager.SendMessage(text);
    }

    [UsedImplicitly]
    [UIAction("division-format")]
    private string DivisionFormat(int value)
    {
        Listing? listing = _listingManager.Listing;
        return listing is { Divisions.Count: > 0 } ? listing.Divisions[value].Name : "N/A";
    }

    [UsedImplicitly]
    [UIAction("replay-intro")]
    private void OnReplayIntroClick()
    {
        IntroStarted?.Invoke();
    }

    [UsedImplicitly]
    [UIAction("replay-outro")]
    private void OnReplayOutroClick()
    {
        OutroStarted?.Invoke();
    }

    private void OnPlayerCountUpdated(int chatters, int online)
    {
        _playerCountText = $"\ud83d\udcac {chatters} / \ud83d\udc64 {online}";
    }

    private void OnStageUpdated(IStageStatus stageStatus)
    {
        UnityMainThreadTaskScheduler.Factory.StartNew(
            () =>
            {
                _replayIntroObject.SetActive(stageStatus is not IntroStatus);
                _replayOutroObject.SetActive(stageStatus is FinishStatus);
            });
    }

    [UsedImplicitly]
    [UIAction("toend-click")]
    private void OnToEndClick()
    {
        _scrollView.ScrollToEnd(true);
    }

    private void OnUserBanned(string id)
    {
        _messageQueue.RemoveAll(n => n.Id == id);

        bool scrollToEnd = AtEnd();
        float heightLost = 0;
        _messages.Where(n => n.ChatMessage.Id == id).Do(
            n =>
            {
                TextMeshProUGUI textMesh = n.TextMesh;
                float height = textMesh.rectTransform.rect.height;
                textMesh.text = "<deleted>";
                heightLost += textMesh.rectTransform.rect.height - height;
            });
        ScrollLostHeight(scrollToEnd, heightLost);
    }

    [UsedImplicitly]
    [UIAction("show-modal")]
    private void ShowModal()
    {
        _modal.Show(true);
    }

    private bool AtEnd()
    {
        float end = _scrollView.contentSize - _scrollView.scrollPageSize;
        return end < 0 || Mathf.Abs(end - _scrollView._destinationPos) < 0.01f;
    }

    private void ScrollLostHeight(bool scrollToEnd, float heightLost)
    {
        Canvas.ForceUpdateCanvases();
        RectTransform contentTransform = _scrollView._contentRectTransform;
        _scrollView.SetContentSize(contentTransform.rect.height);
        float heightDiff;
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

    private void Update()
    {
        if (_playerCountText != _playerCount.text)
        {
            _playerCount.text = _playerCountText;
        }

        if (_priorityMessages.Count > 0)
        {
            float dTime = Time.deltaTime;
            for (int i = _priorityMessages.Count - 1; i >= 0; i--)
            {
                PriorityMessage priorityMessage = _priorityMessages[i];
                priorityMessage.Time -= dTime;
                if (priorityMessage.Time > 0)
                {
                    continue;
                }

                priorityMessage.Text.gameObject.SetActive(false);
                _disabledPriorityMessages.Push(priorityMessage);
                _priorityMessages.Remove(priorityMessage);
            }

            if (_priorityMessages.Count == 0)
            {
                _priorityVertical.gameObject.SetActive(false);
            }
        }

        if (_messageQueue.Count == 0)
        {
            return;
        }

        try
        {
            bool scrollToEnd = AtEnd();
            float heightLost = 0;

            ChatMessage[] queue = _messageQueue.ToArray();
            _messageQueue.Clear();
            foreach (ChatMessage message in queue)
            {
                string content;
                Color color;
                string messageString = message.Message;
                string usernameString = message.Username;
                string? colorString = message.Color;
                if ((message.Type != MessageType.System && message.Type != MessageType.PrioritySystem) &&
                    _config.ProfanityFilter)
                {
                    messageString = _profanityFilter.CensorString(messageString);
                    usernameString = _profanityFilter.CensorString(usernameString);
                }

                switch (message.Type)
                {
                    case MessageType.PrioritySystem:
                    case MessageType.System:
                        content = Colorize(messageString, colorString);
                        color = Color.white;
                        break;

                    case MessageType.WhisperFrom:
                        content = $"[From {Colorize(NoParse(usernameString), colorString)}] {NoParse(messageString)}";
                        color = Color.magenta;
                        break;

                    case MessageType.WhisperTo:
                        content = $"[To {Colorize(NoParse(usernameString), colorString)}] {NoParse(messageString)}";
                        color = Color.magenta;
                        break;

                    case MessageType.Say:
                    default:
                        content = $"[{Colorize(NoParse(usernameString), colorString)}] {NoParse(messageString)}";
                        color = Color.white;
                        break;
                }

                if (message.Type == MessageType.PrioritySystem)
                {
                    _priorityVertical.gameObject.SetActive(true);

                    PriorityMessage priorityMessage;
                    if (_priorityMessages.Count >= 5)
                    {
                        priorityMessage = _priorityMessages.First();
                        TextMeshProUGUI text = priorityMessage.Text;
                        text.color = color;
                        text.text = content;
                        text.transform.SetAsLastSibling();
                    }
                    else if (_disabledPriorityMessages.Count == 0)
                    {
                        TextMeshProUGUI prio =
                            BeatSaberUI.CreateText(
                                (RectTransform)_priorityBg.transform,
                                content,
                                Vector2.zero);
                        prio.enableWordWrapping = true;
                        prio.richText = true;
                        prio.color = color;
                        prio.alignment = TextAlignmentOptions.Left;
                        prio.fontSize = 4;
                        priorityMessage = new PriorityMessage(prio);
                        _priorityMessages.Add(priorityMessage);
                    }
                    else
                    {
                        priorityMessage = _disabledPriorityMessages.Pop();
                        TextMeshProUGUI text = priorityMessage.Text;
                        text.gameObject.SetActive(true);
                        text.color = color;
                        text.text = content;
                        text.transform.SetAsLastSibling();
                        _priorityMessages.Add(priorityMessage);
                    }

                    priorityMessage.Time = 20;
                }

                if (_messages.Count > 100)
                {
                    LinkedListNode<(ChatMessage, TextMeshProUGUI)> first = _messages.First;
                    _messages.RemoveFirst();
                    TextMeshProUGUI text = first.Value.Item2;
                    float height = text.rectTransform.rect.height;
                    text.color = color;
                    text.text = content;
                    text.transform.SetAsLastSibling();
                    first.Value = (message, text);
                    _messages.AddLast(first);
                    heightLost += height;
                }
                else
                {
                    TextMeshProUGUI text =
                        BeatSaberUI.CreateText(
                            (RectTransform)_textObject.transform,
                            content,
                            Vector2.zero);
                    text.enableWordWrapping = true;
                    text.richText = true;
                    text.color = color;
                    text.alignment = TextAlignmentOptions.Left;
                    text.fontSize = 4;
                    _messages.AddLast((message, text));
                }

                ScrollLostHeight(scrollToEnd, heightLost);
            }
        }
        catch (Exception e)
        {
            _log.Error($"Exception while processing message\n{e}");
        }

        return;

        static string NoParse(string message)
        {
            StringBuilder stringBuilder = new(message.Length);
            foreach (char c in message)
            {
                if (c is '<' or '>')
                {
                    stringBuilder.Append($"<noparse>{c}</noparse>");
                }
                else
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString();
        }

        static string Colorize(string message, string? color)
        {
            if (color == null)
            {
                return message;
            }

            return color[0] != '#' ? $"<color=\"{color}\">{message}</color>" : $"<color={color}>{message}</color>";
        }
    }

    private class PriorityMessage
    {
        internal PriorityMessage(TextMeshProUGUI text)
        {
            Text = text;
        }

        internal TextMeshProUGUI Text { get; }

        internal float Time { get; set; }
    }
}
