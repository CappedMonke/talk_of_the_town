using UnityEngine;

public class GlobalSettings : MonoBehaviour
{
    public static GlobalSettings Instance { get; private set; }

    [SerializeField] private LogLevel logLevel = LogLevel.Warning;

    public LogLevel LogLevel
    {
        get => logLevel;
        set { logLevel = value; GameLog.GlobalLevel = value; }
    }
    [SerializeField] private string llmModel = "";
    [SerializeField] private PromptStyle promptStyle = PromptStyle.Normal;

    public string LLMModel
    {
        get => llmModel;
        set => llmModel = value;
    }

    public PromptStyle PromptStyle
    {
        get => promptStyle;
        set => promptStyle = value;
    }

    /// <summary>Kept for backward compatibility with UI toggle. Maps Caveman ↔ Normal.</summary>
    public bool UseCavemanPrompt
    {
        get => promptStyle == PromptStyle.Caveman;
        set => promptStyle = value ? PromptStyle.Caveman : PromptStyle.Normal;
    }

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
        {
            Destroy(gameObject);
            return;
        }

        GameLog.GlobalLevel = logLevel;
    }

    private void OnValidate()
    {
        GameLog.GlobalLevel = logLevel;
    }
}

public enum PromptStyle
{
    Normal,
    Lean,
    Caveman,
    NormalOld,
    CavemanOld
}