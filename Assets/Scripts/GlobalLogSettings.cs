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
    [SerializeField] private bool useCavemanPrompt = false;

    public string LLMModel
    {
        get => llmModel;
        set => llmModel = value;
    }

    public bool UseCavemanPrompt
    {
        get => useCavemanPrompt;
        set => useCavemanPrompt = value;
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