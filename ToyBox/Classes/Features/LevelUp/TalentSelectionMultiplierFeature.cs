using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Kingmaker.GameCommands;
using Kingmaker.Mechanics.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.UI.MVVM.View.ServiceWindows.CharacterInfo.Sections.Careers.Common.CareerPathProgression;
using Kingmaker.UI.MVVM.VM.ServiceWindows.CharacterInfo.Sections.Careers.CareerPath;
using Kingmaker.UI.MVVM.VM.ServiceWindows.CharacterInfo.Sections.Careers.RankEntry;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Levelup;
using Kingmaker.UnitLogic.Levelup.Selections;
using Kingmaker.UnitLogic.Levelup.Selections.Feature;
using Kingmaker.UnitLogic.Progression.Features;
using Kingmaker.UnitLogic.Progression.Paths;

namespace ToyBox.Features.LevelUp;

/// <summary>
/// Multiplies career talent selection slots while preserving Rogue Trader's
/// normal level-up UI, preview, prerequisite checks, commit, save and respec flow.
///
/// A multiplier of 1 is vanilla behavior.
/// </summary>
[HarmonyPatch, ToyBoxPatchCategory(HarmonyId)]
public partial class TalentSelectionMultiplierFeature : FeatureWithIntSlider {
    internal const string HarmonyId = "ToyBox.Features.LevelUp.TalentSelectionMultiplierFeature";
    private readonly Harmony m_Harmony = new(HarmonyId);
    private bool m_IsPatched;

    // Extra RankEntrySelectionVMs use the same BlueprintSelectionFeature as the
    // original slot. This records which matching SelectionState each extra row owns.
    private sealed class ExtraSelectionInfo {
        public readonly BlueprintSelectionFeature Blueprint;
        // 0 = vanilla/original slot, 1 = first extra slot, 2 = second extra slot...
        public readonly int Occurrence;

        public ExtraSelectionInfo(BlueprintSelectionFeature blueprint, int occurrence) {
            Blueprint = blueprint;
            Occurrence = occurrence;
        }
    }

    private static readonly ConditionalWeakTable<RankEntrySelectionVM, ExtraSelectionInfo> s_ExtraSelections = new();

    internal static bool HasExtraSelectionRows(CareerPathRankEntryVM rank) =>
        rank.Selections.Any(row => s_ExtraSelections.TryGetValue(row, out _));

    private sealed class SessionOptions {
        public readonly int Multiplier;
        public SessionOptions(LevelUpManager manager) {
            Multiplier = !manager.AutoCommit && ToyBoxUnitHelper.IsPartyOrPet(manager.TargetUnit)
                ? Math.Max(1, Math.Min(10, Settings.TalentSelectionMultiplier)) : 1;
        }
    }

    // Capture even 1x sessions: moving the slider must not change an existing
    // manager when it recreates selections for another target level or path.
    private static readonly ConditionalWeakTable<LevelUpManager, SessionOptions> s_Sessions = new();

    // Finish serializes choices into a game command and constructs a NEW
    // manager to replay them. That manager must use the command's pick counts,
    // not the slider setting at execution time. Scope this only to construction.
    [ThreadStatic]
    private static CommitLevelUpGameCommand? s_ReplayingCommand;

    [LocalizedString(
        "ToyBox_Features_LevelUp_TalentSelectionMultiplierFeature_Name",
        "Talent Selection Multiplier")]
    public override partial string Name { get; }

    [LocalizedString(
        "ToyBox_Features_LevelUp_TalentSelectionMultiplierFeature_Description",
        "Multiplies selectable career talent slots (1 = vanilla). Changes apply to the next level-up or respec session. Previously chosen talents remain visible at every setting.")]
    public override partial string Description { get; }

    // Keep the hooks installed at 1x too, to display previously multiplied
    // history and finish any session that started with a larger multiplier.
    public override bool IsEnabled => true;

    public override ref int Value => ref Settings.TalentSelectionMultiplier;
    public override int Min => 1;
    public override int Max => 10;
    public override int? Default => 1;

    public override void Enable() {
        base.Enable();
        if (!m_IsPatched) {
            ToyBoxPatchCategoryAttribute.PatchCategory(HarmonyId, m_Harmony);
            m_IsPatched = true;
        }
    }

    public override void Disable() {
        if (m_IsPatched) {
            m_Harmony.UnpatchAll(HarmonyId);
            m_IsPatched = false;
        }
        base.Disable();
    }

    private static bool IsTalentGroup(FeatureGroup group) {
        return group is FeatureGroup.Talent
            or FeatureGroup.CommonTalent
            or FeatureGroup.AscensionTalent
            or FeatureGroup.FirstCareerTalent
            or FeatureGroup.SecondCareerTalent
            or FeatureGroup.FirstOrSecondCareerTalent;
    }

    /// <summary>
    /// LevelUpManager normally creates exactly one SelectionState per blueprint
    /// selection. Create additional independent SelectionStateFeature instances so
    /// every extra pick has its own preview/prerequisite/commit state.
    /// </summary>
    [HarmonyPatch(typeof(LevelUpManager), "CreatePathSelections"), HarmonyPostfix]
    private static void LevelUpManager_CreatePathSelections_Postfix(
        LevelUpManager __instance,
        ref IEnumerable<SelectionState> __result) {

        var command = s_ReplayingCommand;
        if (command != null && __instance.TargetUnit == command.m_UnitRef.Entity.ToBaseUnitEntity()
            && __instance.Path == command.m_CareerPath) {
            __result = ExpandSelections(__instance, __result, 1, command.m_Selections);
            return;
        }
        int multiplier = s_Sessions.GetValue(__instance, manager => new SessionOptions(manager)).Multiplier;
        if (multiplier <= 1) {
            return;
        }

        __result = ExpandSelections(__instance, __result, multiplier);
    }

    private static IEnumerable<SelectionState> ExpandSelections(
        LevelUpManager manager,
        IEnumerable<SelectionState> source,
        int multiplier,
        IReadOnlyList<SelectionEntry>? replay = null) {

        foreach (SelectionState state in source) {
            yield return state;

            if (state is not SelectionStateFeature featureState
                || featureState.Path is not BlueprintCareerPath
                || !IsTalentGroup(featureState.Blueprint.Group)) {
                continue;
            }

            int count = replay == null ? multiplier : Math.Max(1, replay.Count(
                entry => entry.Selection == featureState.Blueprint && entry.PathRank == featureState.PathRank));
            for (int occurrence = 1; occurrence < count; occurrence++) {
                yield return new SelectionStateFeature(
                    manager,
                    featureState.Blueprint,
                    featureState.Path,
                    featureState.PathRank);
            }
        }
    }

    /// <summary>
    /// The career UI also needs one row per extra SelectionState. Insert duplicates
    /// immediately after their vanilla row so UI ordering matches manager ordering.
    /// </summary>
    [HarmonyPatch(
        typeof(CareerPathRankEntryVM),
        MethodType.Constructor,
        new Type[] { typeof(int), typeof(CareerPathVM), typeof(BlueprintPath.RankEntry) }),
     HarmonyPostfix]
    private static void CareerPathRankEntryVM_Constructor_Postfix(
        CareerPathRankEntryVM __instance,
        int rank,
        CareerPathVM careerPathVM,
        BlueprintPath.RankEntry rankEntry) {

        ReconcileRows(__instance, rank, careerPathVM, rankEntry);
    }

    // UnitProgressionVM.SetCareerPath initializes rank entries BEFORE creating
    // the manager. UpdateRanks is also reached after manager creation, selection
    // changes, cancellation and commit, so reconcile the retained rows here.
    [HarmonyPatch(typeof(CareerPathVM), "UpdateRanks"), HarmonyPrefix]
    private static void CareerPathVM_UpdateRanks_Prefix(CareerPathVM __instance, out bool __state) {
        __state = false;
        foreach (CareerPathRankEntryVM entry in __instance.RankEntries) {
            BlueprintPath.RankEntry rankEntry = __instance.CareerPath.GetRankEntry(entry.Rank);
            if (rankEntry != null) {
                __state |= ReconcileRows(entry, entry.Rank, __instance, rankEntry);
            }
        }
        if (__state) {
            __instance.UpdateRankEntriesScan();
        }
    }

    [HarmonyPatch(typeof(CommitLevelUpGameCommand), "ExecuteInternal"), HarmonyPrefix]
    private static bool CommitLevelUpGameCommand_ExecuteInternal_Prefix(CommitLevelUpGameCommand __instance) {
        if (__instance.m_CareerPath is not BlueprintCareerPath) {
            return true;
        }

        LevelUpManager manager;
        var previous = s_ReplayingCommand;
        try {
            s_ReplayingCommand = __instance;
            manager = new LevelUpManager(__instance.m_UnitRef.Entity.ToBaseUnitEntity(),
                __instance.m_CareerPath, autoCommit: false);
        } finally {
            s_ReplayingCommand = previous;
        }

        using (manager) {
            var pending = __instance.m_Selections.GroupBy(entry => (entry.Selection, entry.PathRank))
                .ToDictionary(group => group.Key, group => new Queue<SelectionEntry>(group));
            foreach (SelectionState selection in manager.Selections) {
                if (selection is not SelectionStateFeature state) {
                    throw new InvalidOperationException($"{HarmonyId}: unsupported selection state in commit replay.");
                }
                if (!pending.TryGetValue((state.Blueprint, state.PathRank), out var entries) || entries.Count == 0) {
                    continue; // Vanilla permits unmade states only when CanSelectAny is false.
                }
                // Vanilla FirstItem repeatedly uses occurrence zero. Consume each
                // command entry once, in order, retaining normal Select/validation.
                SelectionEntry entry = entries.Dequeue();
                FeatureSelectionItem item = state.Items.FirstOrDefault(candidate => candidate.Feature == entry.Feature);
                if (item.Feature == null || !state.Select(item)) {
                    throw new InvalidOperationException($"{HarmonyId}: cannot replay talent {entry.Feature} at rank {entry.PathRank}.");
                }
            }
            if (pending.Values.Any(entries => entries.Count != 0)) {
                throw new InvalidOperationException($"{HarmonyId}: commit contains choices without matching selection states.");
            }
            manager.Commit();
        }
        EventBus.RaiseEvent((ILevelUpManagerUIHandler handler) => handler.HandleUICommitChanges());
        return false;
    }

    [HarmonyPatch(typeof(CareerPathVM), "UpdateRanks"), HarmonyPostfix]
    private static void CareerPathVM_UpdateRanks_Postfix(CareerPathVM __instance, bool __state) {
        if (!__state) {
            return;
        }
        // Rebuild navigation after vanilla has refreshed the new rows' states.
        // CreateFeaturesToVisit calls UpdateRanks again; reconciliation is
        // idempotent, so that inner call has __state=false and stops here.
        __instance.CreateFeaturesToVisit();
        __instance.SetupFeaturesFromSelected();
    }

    // The round progression view only draws widgets on Bind; OnUpdateData
    // normally refreshes its bars, not its slot widgets. A retained view must
    // rebuild when session reconciliation adds/removes rows. Do not redraw on
    // ordinary selection changes, to preserve focus and avoid widget churn.
    [HarmonyPatch(typeof(CareerPathRoundProgressionCommonView), "UpdateProgressBar"), HarmonyPrefix]
    private static void CareerPathRoundProgressionCommonView_UpdateProgressBar_Prefix(
        CareerPathRoundProgressionCommonView __instance) {
        if (__instance.GetViewModel() is not CareerPathVM career) {
            return;
        }
        bool changed = __instance.m_RankEntries.Count != career.RankEntries.Count;
        for (int i = 0; !changed && i < career.RankEntries.Count; i++) {
            var view = __instance.m_RankEntries[i];
            var entry = career.RankEntries[i];
            changed = view.GetViewModel() != entry
                || view.GetConsoleEntities().Count != entry.Selections.Count + entry.Features.Count
                || entry.Selections.Any(row => view.TryGetItemByViewModel(row) == null);
        }
        if (changed) {
            __instance.Clear();
            __instance.DrawEntries();
            __instance.UpdateCurrentProgressBar();
        }
    }

    private static bool ReconcileRows(
        CareerPathRankEntryVM entry,
        int rank,
        CareerPathVM careerPathVM,
        BlueprintPath.RankEntry rankEntry) {

        LevelUpManager? manager = careerPathVM.UnitProgressionVM?.LevelUpManager;
        if (manager == null || manager.AutoCommit || manager.TargetUnit != careerPathVM.Unit) {
            manager = null;
        }

        BlueprintSelectionFeature[] blueprints = rankEntry.Selections
            .OfType<BlueprintSelectionFeature>()
            .ToArray();
        RankEntrySelectionVM[] vanillaRows = entry.Selections
            .Where(row => !s_ExtraSelections.TryGetValue(row, out _)).ToArray();

        // If Owlcat changes the constructor contract, fail safe rather than pairing
        // the wrong UI row with the wrong blueprint selection.
        if (blueprints.Length != vanillaRows.Length) {
            Warn($"{HarmonyId}: rank {rank} selection count mismatch; extra talent rows skipped.");
            return false;
        }

        bool changed = false;
        // Work backwards so inserts do not shift the vanilla indices still to visit.
        for (int i = blueprints.Length - 1; i >= 0; i--) {
            BlueprintSelectionFeature blueprint = blueprints[i];
            if (!IsTalentGroup(blueprint.Group)) {
                continue;
            }

            // Saved history is independent of the slider and session. Active
            // choices use actual states, including their original party gate.
            int committed = careerPathVM.Unit.Progression
                .GetSelectionsByPath(careerPathVM.CareerPath)
                .Count(s => s.Level == rank && s.Selection == blueprint);
            int active = manager?.Selections.OfType<SelectionStateFeature>()
                .Count(s => s.Path == careerPathVM.CareerPath
                    && s.PathRank == rank && s.Blueprint == blueprint) ?? 0;
            int extraRowCount = Math.Max(0, Math.Max(committed, active) - 1);
            RankEntrySelectionVM[] existing = entry.Selections.Where(row =>
                s_ExtraSelections.TryGetValue(row, out var info) && info.Blueprint == blueprint).ToArray();

            for (int index = existing.Length - 1; index >= extraRowCount; index--) {
                RankEntrySelectionVM removed = existing[index];
                if (careerPathVM.UnitProgressionVM?.CurrentRankEntryItem.Value == removed) {
                    careerPathVM.UnitProgressionVM.CurrentRankEntryItem.Value = null;
                }
                careerPathVM.FeaturesToVisit.Remove(removed);
                s_ExtraSelections.Remove(removed);
                entry.Selections.RemoveAndDispose(removed);
                changed = true;
            }

            int insertIndex = entry.Selections.IndexOf(vanillaRows[i]) + 1 + existing.Length;
            for (int occurrence = existing.Length + 1; occurrence <= extraRowCount; occurrence++) {
                var extraRow = new RankEntrySelectionVM(
                    rank,
                    careerPathVM,
                    blueprint,
                    entry.SelectRankEntryItem);

                entry.Selections.Insert(insertIndex++, extraRow);
                s_ExtraSelections.Add(extraRow, new ExtraSelectionInfo(blueprint, occurrence));
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Vanilla RankEntrySelectionVM.UpdateState asks LevelUpManager for the first
    /// state matching (path, blueprint, rank). That is correct for the original row
    /// but not for duplicated rows. Only replace UpdateState for rows we created.
    /// </summary>
    [HarmonyPatch(typeof(RankEntrySelectionVM), nameof(RankEntrySelectionVM.UpdateState)), HarmonyPrefix]
    private static bool RankEntrySelectionVM_UpdateState_Prefix(
        RankEntrySelectionVM __instance,
        LevelUpManager levelUpManager) {

        if (!s_ExtraSelections.TryGetValue(__instance, out ExtraSelectionInfo? extra) || extra == null) {
            return true;
        }

        __instance.m_ShowGroupList?.ForEach(vm => vm.UpdateState(levelUpManager));

        SelectionStateFeature? state = levelUpManager?.Selections
            .OfType<SelectionStateFeature>()
            .Where(s => s.Path == __instance.m_CareerPathVM.CareerPath
                && s.Blueprint == extra.Blueprint
                && s.PathRank == __instance.Rank)
            .Skip(extra.Occurrence)
            .FirstOrDefault();

        __instance.m_SelectionStateFeature.Value = state;

        (BlueprintFeature Feature, int Rank)? selectedFeature = GetCommittedSelection(
            __instance.m_CareerPathVM.Unit.Progression,
            __instance.m_CareerPathVM.CareerPath,
            __instance.Rank,
            extra.Blueprint,
            extra.Occurrence);

        if (selectedFeature.HasValue) {
            __instance.EntryState.Value = RankEntryState.Committed;
            __instance.SetSelectedFeature(selectedFeature.Value.Feature);
        } else if (state != null) {
            if (!state.IsValid) {
                __instance.EntryState.Value = RankEntryState.NotValid;
            } else if (state.SelectionItem.HasValue) {
                __instance.EntryState.Value = RankEntryState.Selected;
            } else if (!state.CanSelectAny) {
                __instance.EntryState.Value = RankEntryState.NotSelectable;
            } else if (__instance.m_CareerPathVM.FirstSelectable == __instance) {
                __instance.EntryState.Value = RankEntryState.FirstSelectable;
            } else if (__instance.Rank == __instance.m_CareerPathVM.FirstSelectable?.Rank) {
                __instance.EntryState.Value = RankEntryState.Selectable;
            } else {
                __instance.EntryState.Value = RankEntryState.WaitPreviousToSelect;
            }

            __instance.SetSelectedFeature(state.SelectionItem);
        } else {
            __instance.EntryState.Value = RankEntryState.NotSelectable;
            __instance.SetSelectedFeature((FeatureSelectionItem?)null);
        }

        return false;
    }

    private static (BlueprintFeature Feature, int Rank)? GetCommittedSelection(
        PartUnitProgression progression,
        BlueprintPath path,
        int rank,
        BlueprintSelectionFeature selection,
        int occurrence) {

        FeatureSelectionData data = progression.GetSelectionsByPath(path)
            .Where(s => s.Level == rank && s.Selection == selection)
            .Skip(occurrence)
            .FirstOrDefault();

        return data.Feature == null ? null : (data.Feature, data.Rank);
    }
}
