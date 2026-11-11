using System;
using System.Reflection;
using UnityEngine;

public sealed class LanScoreboard : MonoBehaviour
{
    private static bool created;
    private static Type worldType;
    private static FieldInfo scoreField;
    private static Type stageType;
    private static PropertyInfo stageInstance;
    private static MethodInfo currentStage;
    private Font font;
    private Font styleFont;
    private GUIStyle titleStyle;
    private GUIStyle rowStyle;
    private GUIStyle resultTitleStyle;
    private GUIStyle resultRowStyle;

    private void Awake()
    {
        if (created)
        {
            Destroy(gameObject);
            return;
        }
        created = true;
        DontDestroyOnLoad(gameObject);
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 16);
        worldType = Type.GetType("CS.WorldServerProxy, Assembly-CSharp");
        scoreField = worldType == null ? null : worldType.GetField("LanScoreText", BindingFlags.Public | BindingFlags.Static);
        stageType = Type.GetType("CS.StageManager, Assembly-CSharp");
        stageInstance = stageType == null ? null : stageType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        currentStage = stageType == null ? null : stageType.GetMethod("GetCurrentStage");
    }

    public void SetFont(Font value)
    {
        if (value != null)
            font = value;
    }

    private static bool InBattle()
    {
        if (stageType == null || currentStage == null)
            return false;
        object manager = stageInstance == null ? null : stageInstance.GetValue(null, null);
        if (manager == null)
        {
            var getter = stageType.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.Static);
            manager = getter == null ? null : getter.Invoke(null, null);
        }
        if (manager == null)
            return false;
        var stage = currentStage.Invoke(manager, null);
        return stage != null && stage.GetType().Name == "StageBattle";
    }

    private static string ScoreText()
    {
        return scoreField == null ? null : scoreField.GetValue(null) as string;
    }

    private void BuildStyles()
    {
        if (styleFont == font && titleStyle != null)
            return;
        styleFont = font;

        titleStyle = new GUIStyle(GUI.skin.label);
        titleStyle.font = font;
        titleStyle.fontSize = 18;
        titleStyle.fontStyle = FontStyle.Bold;
        titleStyle.alignment = TextAnchor.MiddleLeft;
        titleStyle.normal.textColor = new Color(1f, 0.84f, 0.34f, 1f);

        rowStyle = new GUIStyle(GUI.skin.label);
        rowStyle.font = font;
        rowStyle.fontSize = 15;
        rowStyle.alignment = TextAnchor.MiddleLeft;
        rowStyle.normal.textColor = Color.white;

        resultTitleStyle = new GUIStyle(titleStyle);
        resultTitleStyle.fontSize = 24;
        resultTitleStyle.alignment = TextAnchor.MiddleCenter;

        resultRowStyle = new GUIStyle(rowStyle);
        resultRowStyle.fontSize = 19;
        resultRowStyle.alignment = TextAnchor.MiddleCenter;
    }

    private static void Panel(Rect rect, float alpha)
    {
        var previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, alpha);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private void OnGUI()
    {
        if (!InBattle())
            return;
        var text = ScoreText();
        if (string.IsNullOrEmpty(text))
            return;
        BuildStyles();

        var final = text.StartsWith("FINAL|", StringComparison.Ordinal);
        var body = text.IndexOf('|') >= 0 ? text.Substring(text.IndexOf('|') + 1) : text;
        var entries = string.IsNullOrEmpty(body) ? new string[0] : body.Split(';');
        if (final)
        {
            DrawResult(entries);
            return;
        }

        var height = 40 + entries.Length * 21;
        var box = new Rect(12, 270, 300, height);
        Panel(box, 0.62f);
        GUI.Label(new Rect(box.x + 12, box.y + 6, 276, 26), "\u51fb \u6740 \u699c", titleStyle);
        for (var i = 0; i < entries.Length; i++)
        {
            var fields = entries[i].Split(',');
            if (fields.Length < 2)
                continue;
            GUI.Label(new Rect(box.x + 14, box.y + 32 + i * 21, 272, 20),
                (i + 1) + ". " + fields[0] + "    " + fields[1], rowStyle);
        }
    }

    private void DrawResult(string[] entries)
    {
        var box = new Rect((Screen.width - 440) / 2, (Screen.height - 250) / 2, 440, 250);
        Panel(box, 0.72f);
        GUI.Label(new Rect(box.x + 20, box.y + 14, 400, 34), "\u5bf9\u5c40\u7ed3\u675f", resultTitleStyle);
        GUI.Label(new Rect(box.x + 20, box.y + 52, 400, 26), "\u51fb \u6740 \u699c  \u00b7  \u524d\u4e09\u540d", titleStyle);
        var shown = Math.Min(3, entries.Length);
        for (var i = 0; i < shown; i++)
        {
            var fields = entries[i].Split(',');
            if (fields.Length < 2)
                continue;
            GUI.Label(new Rect(box.x + 20, box.y + 90 + i * 40, 400, 32),
                (i + 1) + ". " + fields[0] + "    " + fields[1] + " \u6740", resultRowStyle);
        }
    }
}



