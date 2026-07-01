using Heathen.Editor;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Reports to the Game Framework when the baked story code is behind the <c>.ogham</c> sources, so the
    /// <see cref="StorytellerSubsystem"/> shows a "Build" attention chip on Project ▸ Subsystems (and in the
    /// play-mode guard / Scene-view overlay). Reuses the registered "Ogham Stories" generator so it always agrees
    /// with the shared build pipeline.
    /// </summary>
    public sealed class OghamSubsystemHealth : ISubsystemHealth
    {
        public Type SubsystemType => typeof(StorytellerSubsystem);

        public IEnumerable<SubsystemIssue> GetIssues()
        {
            var generator = SettingsGenerators.All.FirstOrDefault(g => g.Name == "Ogham Stories");
            if (generator != null && generator.IsStale())
                yield return new SubsystemIssue(
                    SubsystemHealthSeverity.Warning,
                    "Story code is out of date. Build to apply your latest .ogham changes.",
                    "Build",
                    () => { generator.Generate(); AssetDatabase.Refresh(); });
        }
    }
}
