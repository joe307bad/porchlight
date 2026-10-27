// Runs the generation pipeline. Pure: no engine, no rendering, no
// System.Random, so it runs the same in Unity, on a console, or in a .NET test.

using System;
using System.Collections.Generic;
using Porchlight.Core;
using Porchlight.Model;

namespace Porchlight.Gen
{
    /// <summary>One pipeline step: a pure function that returns the model with one more layer.</summary>
    public interface IStage
    {
        string Name { get; }
        TownModel Run(TownModel model, Rng rng);
        /// <summary>Invariants that must hold after this stage; returns human-readable violations.</summary>
        List<string> Check(TownModel model);
    }

    public sealed class InvariantException : Exception
    {
        public readonly string Stage;
        public readonly List<string> Violations;

        public InvariantException(string stage, List<string> violations)
            : base($"[{stage}] {string.Join("; ", violations)}")
        {
            Stage = stage;
            Violations = violations;
        }
    }

    public static class Generator
    {
        /// <summary>The pipeline, in order. Each stage adds one layer and gets its own RNG stream.</summary>
        public static readonly IStage[] Stages =
        {
            new TerrainStage(),
            new RiversStage(),
            new SiteStage(),
            new RoadsStage(),
            new StreetsStage(),
            new BlocksStage(),
            new LotsStage(),
            new BuildingsStage(),
            new RasterizeStage(),
        };

        public static TownModel Generate(string seed, GenParams @params, bool check = false, string until = null)
        {
            var model = TownModel.CreateEmpty(seed, GenParams.Normalize(@params ?? new GenParams()));
            foreach (var stage in Stages)
            {
                model = stage.Run(model, Rng.Stage(seed, stage.Name));
                if (check)
                {
                    var violations = stage.Check(model);
                    if (violations.Count > 0) throw new InvariantException(stage.Name, violations);
                }
                if (stage.Name == until) break;
            }
            return model;
        }
    }
}
