using System.IO;
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

    private Button readyButton;
    private Button startButton;
    private Text readyLabel;
    private Text statusLabel;

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
    }

    public static void Receive(BinaryReader reader)
    {
        readyCount = reader.ReadByte();
        memberCount = reader.ReadByte();
        capacity = reader.ReadByte();
        isReady = reader.ReadBoolean();
        isOwner = reader.ReadBoolean();
        started = reader.ReadBoolean();
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
        launch.transform.Find("Text").GetComponent<Text>().text = "\u5f00\u59cb\u6e38\u620f";
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

        var view = ready.gameObject.AddComponent<LanRoomUI>();
        view.readyButton = ready;
        view.startButton = launch;
        view.readyLabel = label;
        view.statusLabel = status;
        view.Update();
    }

    private void Update()
    {
        readyButton.interactable = capacity > 0 && !started;
        startButton.gameObject.SetActive(isOwner);
        startButton.interactable = isReady && !started;
        readyLabel.text = isReady ? "\u53d6\u6d88\u51c6\u5907" : isOwner ? "\u51c6\u5907" : "\u5f00\u59cb\u6e38\u620f";
        if (capacity == 0)
        {
            statusLabel.text = "\u6b63\u5728\u8fde\u63a5\u623f\u95f4";
            return;
        }
        var caption = started
            ? "\u672c\u5c40\u8fdb\u884c\u4e2d"
            : (isReady ? "\u5339\u914d\u4e2d" + new string('.', 1 + (int)(Time.unscaledTime * 3) % 3) : "\u7b49\u5f85\u51c6\u5907");
        statusLabel.text = started ? caption : string.Format("{0}  {1}/{2} \u4eba\u51c6\u5907  (\u4e0a\u9650 {3})",
                                                           caption, readyCount, memberCount, capacity);
    }
}
