using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    // MonoBehaviour wrapper around OghamProcessorCore.
    // Handles Unity lifecycle and exposes the full processor API with
    // serialized fields and inspector-visible events.
    //
    // Use OghamProcessorCore directly in ECS managed systems.
    public class OghamProcessor : MonoBehaviour
    {
        // Static accessor. Override-able so multiple processors can coexist
        // (split-screen, simultaneous conversations, etc.).
        public static OghamProcessor Current { get; protected set; }

        // Primary runtime data source — the compiled story produced by the Ogham build
        // processor. Set this in the inspector. Leave null to fall back to _autoRegister.
        [SerializeField] private OghamCompiledData _compiledStory;

        // Fallback for editor iteration: individual OghamData assets registered directly.
        // Ignored when _compiledStory is assigned.
        [SerializeField] private List<OghamData> _autoRegister = new();
        [SerializeField] private UnityEvent<DialogueEntry, List<DialogueOption>> _onDialogueEntered = new();
        [SerializeField] private UnityEvent<bool> _onDialogueClosed = new();

        public event Action<DialogueEntry, List<DialogueOption>> OnDialogueEntered;
        public event Action<bool> OnDialogueClosed;

        private OghamProcessorCore _core;

        // ── Asset registration ────────────────────────────────────────────────

        public void RegisterData(OghamData data)            => _core.RegisterData(data);
        public void RegisterData(OghamCompiledData data)    => _core.RegisterData(data);
        public void UnregisterData(OghamData data)          => _core.UnregisterData(data);
        public void UnregisterData(OghamCompiledData data)  => _core.UnregisterData(data);
        public void UnregisterAll()                          => _core.UnregisterAll();

        // ── Conversation ──────────────────────────────────────────────────────

        public bool StartConversation(GameplayTag tag)            => _core.StartConversation(tag);
        public bool SelectOption(GameplayTag tag)                  => _core.SelectOption(tag);
        public bool SelectOptionByTag(GameplayTag tag)             => _core.SelectOptionByTag(tag);
        public void CloseConversation(bool interrupted = false)    => _core.CloseConversation(interrupted);
        public bool ReturnTo(GameplayTag tag)                      => _core.ReturnTo(tag);

        // ── Query ─────────────────────────────────────────────────────────────

        public bool IsConversationActive                           => _core.IsConversationActive;
        public DialogueEntry CurrentEntry                          => _core.CurrentEntry;
        public IReadOnlyList<HistoryEntry> History                 => _core.History;
        public GameplayTagCollection NarrativeState                => _core.NarrativeState;
        public List<DialogueOption> GetAvailableOptions()          => _core.GetAvailableOptions();
        public DialogueEntry FindEntry(GameplayTag tag)            => _core.FindEntry(tag);

        // ── Save / Load ───────────────────────────────────────────────────────

        public OghamSaveState CreateSaveState(string name)        => _core.CreateSaveState(name);
        public void LoadSaveState(OghamSaveState state)            => _core.LoadSaveState(state);
        public void ClearState()                                   => _core.ClearState();
        public void ApplyOperation(GameplayTagOperation op)        => _core.ApplyOperation(op);

        // ── Unity lifecycle ───────────────────────────────────────────────────

        protected virtual void Awake()
        {
            _core = new OghamProcessorCore();
            _core.OnDialogueEntered += HandleEntered;
            _core.OnDialogueClosed  += HandleClosed;

            if (_compiledStory != null)
                _core.RegisterData(_compiledStory);
            else
                foreach (var data in _autoRegister)
                    if (data != null) _core.RegisterData(data);

            if (Current == null) Current = this;
        }

        protected virtual void OnDestroy()
        {
            if (_core != null)
            {
                _core.OnDialogueEntered -= HandleEntered;
                _core.OnDialogueClosed  -= HandleClosed;
            }
            if (Current == this) Current = null;
        }

        private void HandleEntered(DialogueEntry entry, List<DialogueOption> options)
        {
            OnDialogueEntered?.Invoke(entry, options);
            _onDialogueEntered?.Invoke(entry, options);
        }

        private void HandleClosed(bool interrupted)
        {
            OnDialogueClosed?.Invoke(interrupted);
            _onDialogueClosed?.Invoke(interrupted);
        }
    }
}
