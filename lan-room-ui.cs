using System;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

public sealed class LanRoomUI : MonoBehaviour
{
    private static int readyCount;
    private static int memberCount;
    private static int capacity;
    private static bool isReady;
    private static bool isOwner;
    private static bool started;
    private static int listedCount;
    private static readonly string[] memberNames = new string[8];
    private static readonly bool[] memberReady = new bool[8];
    private static readonly bool[] memberOwner = new bool[8];

    private Button readyButton;
    private Button startButton;
    private Button renameButton;
    private Text readyLabel;
    private Text statusLabel;
    private Text memberLabel;
    private bool renaming;
    private string editName = string.Empty;
    private string renameMessage = string.Empty;
    private Button capMinusButton;
    private Button capPlusButton;
    private Button roundButton;
    private static readonly int[] roundChoices = { 60, 120, 300, 30 };
    private static int roundSeconds = 120;

    public static string GetLoginHost(string fallback)
    {
        const string prefix = "-lan-host=";
        foreach (var argument in System.Environment.GetCommandLineArgs())
        {
            if (argument.StartsWith(prefix, System.StringComparison.Ordinal) && argument.Length > prefix.Length)
                return argument.Substring(prefix.Length);
        }
        return fallback;
    }

    public static void ResetState()
    {
        readyCount = memberCount = capacity = 0;
        isReady = isOwner = started = false;
        listedCount = 0;
        Array.Clear(memberNames, 0, memberNames.Length);
        Array.Clear(memberReady, 0, memberReady.Length);
        Array.Clear(memberOwner, 0, memberOwner.Length);
    }

    public static void Receive(BinaryReader reader)
    {
        readyCount = reader.ReadByte();
        memberCount = reader.ReadByte();
        capacity = reader.ReadByte();
        isReady = reader.ReadBoolean();
        isOwner = reader.ReadBoolean();
        started = reader.ReadBoolean();
        listedCount = reader.ReadUInt16();
        if (listedCount > 8)
            listedCount = 8;
        for (var i = 0; i < listedCount; i++)
        {
            reader.ReadUInt32();
            memberReady[i] = reader.ReadBoolean();
            memberOwner[i] = reader.ReadBoolean();
            var length = reader.ReadUInt16();
            memberNames[i] = Encoding.Unicode.GetString(reader.ReadBytes(length));
        }
    }

    public static void Attach(Button ready, Text label, UnityAction start)
    {
        var root = ready.transform.parent;
        var launch = Instantiate(ready);
        launch.name = "LanHostStart";
        launch.transform.SetParent(root, false);
        var launchRect = (RectTransform)launch.transform;
        launchRect.anchoredPosition = ((RectTransform)ready.transform).anchoredPosition + new Vector2(-320, 0);
        launch.onClick = new Button.ButtonClickedEvent();
        launch.onClick.AddListener(start);
        var launchText = launch.transform.Find("Text").GetComponent<Text>();
        launchText.text = "\u5f00\u59cb\u6e38\u620f";
        launchText.font = label.font;
        launchText.fontSize = 20;
        launchText.color = label.color;
        launchText.enabled = true;
        launch.gameObject.SetActive(false);

        var status = Instantiate(label);
        status.name = "LanRoomStatus";
        status.transform.SetParent(root, false);
        var rect = status.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(1, 0);
        rect.pivot = new Vector2(1, 0);
        rect.anchoredPosition = new Vector2(-12, 191);
        rect.sizeDelta = new Vector2(360, 38);
        status.fontSize = 23;
        status.color = Color.white;
        status.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(2, -2);
        status.resizeTextForBestFit = false;
        status.alignment = TextAnchor.MiddleRight;
        status.raycastTarget = false;

        // Clone the rename button before adding LanRoomUI to the ready button;
        // otherwise the clone inherits a second component with null fields.
        var rename = Instantiate(ready);
        rename.name = "LanRename";
        var canvas = ready.GetComponentInParent<Canvas>();
        rename.transform.SetParent(canvas != null ? canvas.transform : root, false);
        rename.transform.SetAsLastSibling();
        var renameRect = (RectTransform)rename.transform;
        renameRect.anchorMin = renameRect.anchorMax = new Vector2(0, 0);
        renameRect.pivot = new Vector2(0, 0);
        renameRect.anchoredPosition = new Vector2(24, 165);
        renameRect.sizeDelta = new Vector2(170, 48);
        var oldRenameText = rename.transform.Find("Text");
        if (oldRenameText != null)
        {
            var old = oldRenameText.GetComponent<Text>();
            if (old != null)
                old.text = "";
        }
        var renameLabel = Instantiate(label);
        renameLabel.name = "RenameText";
        renameLabel.transform.SetParent(rename.transform, false);
        var renameLabelRect = renameLabel.rectTransform;
        renameLabelRect.anchorMin = Vector2.zero;
        renameLabelRect.anchorMax = Vector2.one;
        renameLabelRect.offsetMin = Vector2.zero;
        renameLabelRect.offsetMax = Vector2.zero;
        renameLabel.text = "\u6539\u540d";
        renameLabel.font = label.font;
        renameLabel.fontSize = 24;
        renameLabel.color = label.color;
        renameLabel.alignment = TextAnchor.MiddleCenter;
        renameLabel.raycastTarget = false;
        renameLabel.enabled = true;

        var members = Instantiate(label);
        members.name = "LanRoomMembers";
        members.transform.SetParent(root, false);
        var membersRect = members.rectTransform;
        membersRect.anchorMin = membersRect.anchorMax = new Vector2(1, 1);
        membersRect.pivot = new Vector2(1, 1);
        membersRect.anchoredPosition = new Vector2(-12, -150);
        membersRect.sizeDelta = new Vector2(360, 250);
        members.fontSize = 18;
        members.color = Color.white;
        members.alignment = TextAnchor.UpperRight;
        members.resizeTextForBestFit = false;
        members.raycastTarget = false;
        members.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(1, -1);

        var view = ready.gameObject.AddComponent<LanRoomUI>();
        view.readyButton = ready;
        view.startButton = launch;
        view.readyLabel = label;
        view.statusLabel = status;
        view.memberLabel = members;
        rename.onClick = new Button.ButtonClickedEvent();
        rename.onClick.AddListener(() =>
        {
            view.renaming = true;
            view.editName = string.Empty;
            view.renameMessage = string.Empty;
        });
        view.renameButton = rename;

        var capMinus = Instantiate(ready);
        capMinus.name = "LanCapMinus";
        capMinus.transform.SetParent(canvas != null ? canvas.transform : root, false);
        capMinus.transform.SetAsLastSibling();
        var capMinusRect = (RectTransform)capMinus.transform;
        capMinusRect.anchorMin = capMinusRect.anchorMax = new Vector2(0, 0);
        capMinusRect.pivot = new Vector2(0, 0);
        capMinusRect.anchoredPosition = new Vector2(210, 165);
        capMinusRect.sizeDelta = new Vector2(56, 48);
        SetCloneText(capMinus, label, "-");

        var capPlus = Instantiate(ready);
        capPlus.name = "LanCapPlus";
        capPlus.transform.SetParent(canvas != null ? canvas.transform : root, false);
        capPlus.transform.SetAsLastSibling();
        var capPlusRect = (RectTransform)capPlus.transform;
        capPlusRect.anchorMin = capPlusRect.anchorMax = new Vector2(0, 0);
        capPlusRect.pivot = new Vector2(0, 0);
        capPlusRect.anchoredPosition = new Vector2(278, 165);
        capPlusRect.sizeDelta = new Vector2(56, 48);
        SetCloneText(capPlus, label, "+");

        var round = Instantiate(ready);
        round.name = "LanRound";
        round.transform.SetParent(canvas != null ? canvas.transform : root, false);
        round.transform.SetAsLastSibling();
        var roundRect = (RectTransform)round.transform;
        roundRect.anchorMin = roundRect.anchorMax = new Vector2(0, 0);
        roundRect.pivot = new Vector2(0, 0);
        roundRect.anchoredPosition = new Vector2(350, 165);
        roundRect.sizeDelta = new Vector2(190, 48);
        SetCloneText(round, label, RoundText());

        capMinus.onClick = new Button.ButtonClickedEvent();
        capMinus.onClick.AddListener(() => SendRoomConfig(capacity - 1, roundSeconds));
        capPlus.onClick = new Button.ButtonClickedEvent();
        capPlus.onClick.AddListener(() => SendRoomConfig(capacity + 1, roundSeconds));
        round.onClick = new Button.ButtonClickedEvent();
        round.onClick.AddListener(() =>
        {
            var index = System.Array.IndexOf(roundChoices, roundSeconds);
            SendRoomConfig(capacity, roundChoices[(index + 1) % roundChoices.Length]);
        });
        view.capMinusButton = capMinus;
        view.capPlusButton = capPlus;
        view.roundButton = round;

        view.Update();
        var scoreboard = new GameObject("LanScoreboard");
        DontDestroyOnLoad(scoreboard);
        var scoreboardView = scoreboard.AddComponent<LanScoreboard>();
        scoreboardView.SetFont(label.font);
    }

    private void Update()
    {
        readyButton.interactable = capacity > 0 && !started;
        startButton.gameObject.SetActive(isOwner);
        startButton.interactable = isReady && !started;
        if (renameButton != null)
            renameButton.gameObject.SetActive(capacity > 0 && !started);
        var configVisible = capacity > 0 && !started && isOwner;
        if (capMinusButton != null)
            capMinusButton.gameObject.SetActive(configVisible);
        if (capPlusButton != null)
            capPlusButton.gameObject.SetActive(configVisible);
        if (roundButton != null)
        {
            roundButton.gameObject.SetActive(configVisible);
            var roundText = roundButton.transform.Find("RenameText");
            if (roundText != null && roundText.GetComponent<Text>() != null)
                roundText.GetComponent<Text>().text = RoundText();
        }
        if (capacity > 0 && !started && Input.GetKeyDown(KeyCode.F2))
        {
            renaming = true;
            editName = string.Empty;
            renameMessage = string.Empty;
        }
        if (configVisible)
        {
            // keyboard shortcuts for the config controls: synthetic mouse
            // clicks are unreliable on this DPI/multi-monitor layout, while
            // Input.GetKeyDown reads the global keyboard state
            if (Input.GetKeyDown(KeyCode.F3))
                SendRoomConfig(capacity + 1, roundSeconds);
            if (Input.GetKeyDown(KeyCode.F5))
                SendRoomConfig(capacity - 1, roundSeconds);
            if (Input.GetKeyDown(KeyCode.F4))
            {
                var index = System.Array.IndexOf(roundChoices, roundSeconds);
                SendRoomConfig(capacity, roundChoices[(index + 1) % roundChoices.Length]);
            }
        }
        readyLabel.text = isReady ? "\u53d6\u6d88\u51c6\u5907" : "\u51c6\u5907";
        if (capacity == 0)
        {
            statusLabel.text = "\u6b63\u5728\u8fde\u63a5\u623f\u95f4";
            return;
        }
        var caption = started
            ? "\u672c\u5c40\u8fdb\u884c\u4e2d"
            : (isReady ? "\u5339\u914d\u4e2d" + new string('.', 1 + (int)(Time.unscaledTime * 3) % 3) : "\u7b49\u5f85\u51c6\u5907");
        statusLabel.text = started ? caption : string.Format("{0}  {1}/{2} \u4eba\u51c6\u5907  (\u4e0a\u9650 {3})  回合 {4}s",
                                                           caption, readyCount, memberCount, capacity, roundSeconds);
        if (memberLabel != null)
        {
            if (capacity == 0 || started || listedCount == 0)
            {
                memberLabel.text = "";
            }
            else
            {
                var builder = new StringBuilder();
                for (var i = 0; i < listedCount; i++)
                {
                    if (i > 0)
                        builder.Append('\n');
                    builder.Append(memberNames[i]);
                    if (memberOwner[i])
                        builder.Append("  \u623f\u4e3b");
                    builder.Append(memberReady[i] ? "  \u5df2\u51c6\u5907" : "  \u672a\u51c6\u5907");
                }
                memberLabel.text = builder.ToString();
            }
        }
    }

    private void OnDisable()
    {
        renaming = false;
        if (renameButton != null)
            renameButton.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (renameButton != null)
            Destroy(renameButton.gameObject);
    }

    private void OnGUI()
    {
        if (!renaming)
            return;
        if (statusLabel != null && statusLabel.font != null)
            GUI.skin.font = statusLabel.font;
        GUI.skin.box.fontSize = 20;
        GUI.skin.label.fontSize = 18;
        GUI.skin.button.fontSize = 18;
        GUI.skin.textField.fontSize = 20;

        var box = new Rect((Screen.width - 380) / 2, (Screen.height - 220) / 2, 380, 220);
        GUI.Box(box, "\u4fee\u6539\u6635\u79f0");
        editName = GUI.TextField(new Rect(box.x + 20, box.y + 54, 340, 34), editName, 16);
        if (renameMessage.Length > 0)
            GUI.Label(new Rect(box.x + 20, box.y + 96, 340, 28), renameMessage);

        if (GUI.Button(new Rect(box.x + 80, box.y + 150, 90, 34), "\u4fdd\u5b58"))
        {
            var name = (editName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                renameMessage = "\u6635\u79f0\u4e0d\u80fd\u4e3a\u7a7a";
            }
            else if (name.Length > 16)
            {
                renameMessage = "\u6700\u591a 16 \u4e2a\u5b57\u7b26";
            }
            else
            {
                SendNickname(name);
                renaming = false;
                renameMessage = string.Empty;
            }
        }
        if (GUI.Button(new Rect(box.x + 210, box.y + 150, 90, 34), "\u53d6\u6d88"))
        {
            renaming = false;
            renameMessage = string.Empty;
        }
    }

    private static string RoundText()
    {
        return "回合 " + roundSeconds + "s";
    }

    private static void SetCloneText(Button clone, Text label, string value)
    {
        var oldText = clone.transform.Find("Text");
        if (oldText != null)
        {
            var old = oldText.GetComponent<Text>();
            if (old != null)
                old.text = "";
        }
        var text = Instantiate(label);
        text.name = "RenameText";
        text.transform.SetParent(clone.transform, false);
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = Vector2.zero;
        text.rectTransform.offsetMax = Vector2.zero;
        text.text = value;
        text.font = label.font;
        text.fontSize = 24;
        text.color = label.color;
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;
        text.enabled = true;
    }

    private static void SendRoomConfig(int cap, int seconds)
    {
        if (cap < 2 || cap > 8 || seconds < 30 || seconds > 600)
            return;
        roundSeconds = seconds;
        try
        {
            var type = Type.GetType("CS.WorldServerProxy, Assembly-CSharp");
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType("CS.WorldServerProxy");
                    if (type != null)
                        break;
                }
            }
            if (type == null)
                return;
            var method = type.GetMethod("SendLanRoomConfigValue", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(ushort), typeof(ushort) }, null);
            if (method != null)
                method.Invoke(null, new object[] { (ushort)cap, (ushort)seconds });
        }
        catch (Exception)
        {
        }
    }

    private static void SendNickname(string name)
    {
        try
        {
            var type = Type.GetType("CS.WorldServerProxy, Assembly-CSharp");
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType("CS.WorldServerProxy");
                    if (type != null)
                        break;
                }
            }
            if (type == null)
                return;
            var method = type.GetMethod("SendLanNicknameValue", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            if (method != null)
                method.Invoke(null, new object[] { name });
        }
        catch (Exception)
        {
        }
    }
}






