using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace MainMenu
{
    public class SettingsMenu : MonoBehaviour
    {
        [SerializeField] private GameObject mainMenu;
        [SerializeField] private GameObject goalsMenu;

        [Header("Prompt Style")]
        [SerializeField] private TMP_Dropdown promptStyleDropdown;

        [Header("Legacy (kept for backward compat — remove once dropdown is wired)")]
        [SerializeField] private Toggle cavemanPromptToggle;

        private void OnEnable()
        {
            if (promptStyleDropdown != null && GlobalSettings.Instance != null)
            {
                promptStyleDropdown.ClearOptions();
                promptStyleDropdown.AddOptions(new System.Collections.Generic.List<string>(Enum.GetNames(typeof(PromptStyle))));
                promptStyleDropdown.SetValueWithoutNotify((int)GlobalSettings.Instance.PromptStyle);
            }

            // Legacy toggle fallback
            if (cavemanPromptToggle != null && GlobalSettings.Instance != null)
                cavemanPromptToggle.SetIsOnWithoutNotify(GlobalSettings.Instance.UseCavemanPrompt);
        }

        public void OnPromptStyleChanged(int index)
        {
            if (GlobalSettings.Instance != null)
                GlobalSettings.Instance.PromptStyle = (PromptStyle)index;
        }

        public void OnCavemanPromptToggleChanged(bool value)
        {
            if (GlobalSettings.Instance != null)
                GlobalSettings.Instance.UseCavemanPrompt = value;
        }

        public void OnBackPressed()
        {
            gameObject.SetActive(false);
            mainMenu.SetActive(true);
        }

        public void OnGoalsPressed()
        {
            gameObject.SetActive(false);
            goalsMenu.SetActive(true);
        }
    }
}
