using Kingmaker.UI.MVVM.View.ServiceWindows.CharacterInfo.Sections.Careers.Common.CareerPathProgression.Items;
using Kingmaker.UI.MVVM.VM.ServiceWindows.CharacterInfo.Sections.Careers.RankEntry;
using UnityEngine;

namespace ToyBox.Features.LevelUp;

[HarmonyPatch, ToyBoxPatchCategory(TalentSelectionMultiplierFeature.HarmonyId)]
internal static class TalentSelectionMultiplierLayout {
    // Compact only ranks containing our additional slots. Keep vanilla's
    // per-item taper and layout calculation, then reduce the final dimensions.
    // CorrectItemSize assigns sizeDelta first, so redraws cannot compound this.
    [HarmonyPatch(typeof(RankEntryItemCommonView), "CorrectItemSize"), HarmonyPostfix]
    private static void RankEntryItemCommonView_CorrectItemSize_Postfix(
        RankEntryItemCommonView __instance, GameObject item) {
        if (__instance.GetViewModel() is not CareerPathRankEntryVM rank
            || !TalentSelectionMultiplierFeature.HasExtraSelectionRows(rank)) {
            return;
        }
        RectTransform rect = item.GetComponent<RectTransform>();
        if (rect != null) {
            rect.sizeDelta *= 0.65f;
        }
    }
}
