using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DarkRealms.World
{
    // ======================================================================================
    //  Map facts: everything a filter may ask about one map, derived once per evaluation.
    //  A filter never reads raw arrays itself, which is what keeps "unknown data" handling in
    //  one place instead of being re-decided by every predicate.
    // ======================================================================================

    public sealed class MapFacts
    {
        public MapDefinition Map;
        public AtlasQueryContext Context;

        public HashSet<string> Tags = new HashSet<string>(StringComparer.Ordinal);
        public ChestSummary Chests;
        public DangerRange Danger;
        public readonly List<string> EnemySpecies = new List<string>();

        public bool HasDungeon;
        public bool HasTown;
        public bool HasBoss;
        public bool HasGuild;
        public bool HasAltar;
        public bool HasShop;
        public bool HasStoryMarker;
        public bool HasNpcPathAction;
        public bool HasNpcRouteData;

        /// <summary>A route is only "known" when progress says so; authored data alone is not knowledge.</summary>
        public bool HasKnownNpcRoute;

        public bool IsVisited;
        public bool IsDiscovered;
        public bool IsCurrent;
        public bool IsFavorite;

        public int ChestsDiscovered;
        public int ChestsOpened;

        public MapCompletion Completion;

        public readonly List<MapPoiDefinition> StoryMarkers = new List<MapPoiDefinition>();

        /// <summary>Route reachability from the query's current map, filled on demand only.</summary>
        public bool RouteEvaluated;
        public bool IsReachable;
        public string RouteNote = string.Empty;

        public string Id => Map != null ? Map.id : string.Empty;
        public string DisplayName => Map != null ? Map.displayName : "(unknown map)";

        public string Breadcrumb => Context != null && Context.Atlas != null
            ? Context.Atlas.LocationBreadcrumb(Id)
            : DisplayName;

        public string Summary =>
            $"{Breadcrumb} | {Chests.Total} chest(s) | {(Danger.IsKnown ? Danger.ToString() : "danger unknown")}" +
            $" | {Completion}";
    }

    /// <summary>Everything a query needs: the authored atlas, the player's progress and where the player is.</summary>
    public sealed class AtlasQueryContext
    {
        public readonly WorldAtlasDefinition Atlas;
        public readonly WorldProgress Progress;
        public readonly string CurrentMapId;

        /// <summary>Optional override for day/night when evaluating conditions in a test.</summary>
        public bool? NightOverride;

        public AtlasQueryContext(WorldAtlasDefinition atlas, WorldProgress progress, string currentMapId)
        {
            Atlas = atlas;
            Progress = progress;
            CurrentMapId = currentMapId ?? string.Empty;
        }

        public MapFacts Describe(string mapId)
        {
            var facts = new MapFacts { Map = Atlas.GetMap(mapId), Context = this };
            if (facts.Map == null)
                return facts;

            facts.Tags = Atlas.EffectiveTagsOf(mapId);
            facts.Chests = Atlas.ChestSummaryOf(mapId);
            facts.Danger = Atlas.DangerRangeOf(mapId);
            facts.EnemySpecies.AddRange(Atlas.EnemySpeciesOf(mapId));
            facts.Completion = CompletionOf(mapId);

            IReadOnlyList<MapPoiDefinition> pois = Atlas.PoisOf(mapId);
            for (int i = 0; i < pois.Count; i++)
            {
                MapPoiDefinition poi = pois[i];
                switch (poi.kind)
                {
                    case PoiKind.Dungeon:
                    case PoiKind.Cave: facts.HasDungeon = true; break;
                    case PoiKind.Town: facts.HasTown = true; break;
                    case PoiKind.Boss: facts.HasBoss = true; break;
                    case PoiKind.Guild: facts.HasGuild = true; break;
                    case PoiKind.Altar: facts.HasAltar = true; break;
                    case PoiKind.Shop:
                    case PoiKind.Inn: facts.HasShop = true; break;
                    case PoiKind.MainStory:
                    case PoiKind.SideStory:
                    case PoiKind.CrossedPath:
                        facts.HasStoryMarker = true;
                        facts.StoryMarkers.Add(poi);
                        break;
                    case PoiKind.NpcLocation:
                        if (!string.IsNullOrEmpty(poi.pathAction))
                            facts.HasNpcPathAction = true;
                        break;
                }

                if (poi.IsChest)
                {
                    DiscoveryState state = Progress != null ? Progress.PoiState(poi.id) : DiscoveryState.Undiscovered;
                    if (state == DiscoveryState.Discovered)
                        facts.ChestsDiscovered++;
                    else if (state == DiscoveryState.Completed)
                        facts.ChestsOpened++;
                }
            }

            IReadOnlyList<NpcRouteDefinition> routes = Atlas.NpcRoutesOf(mapId);
            for (int i = 0; i < routes.Count; i++)
            {
                NpcRouteDefinition route = routes[i];
                if (route == null || !route.HasUsableRoute)
                    continue;

                facts.HasNpcRouteData = true;
                bool known = Progress != null && Progress.IsNpcRouteKnown(route.id);
                if (route.knownByDefault)
                    known = true;

                if (known)
                    facts.HasKnownNpcRoute = true;
            }

            facts.IsVisited = Progress != null && Progress.HasVisited(mapId);
            facts.IsDiscovered = Progress != null && Progress.IsDiscovered(mapId);
            facts.IsCurrent = string.Equals(CurrentMapId, mapId, StringComparison.Ordinal);
            facts.IsFavorite = Progress != null && Progress.IsFavorite(mapId);

            return facts;
        }

        /// <summary>Map completion, computed the same way the runtime computes it so the list and the details
        /// panel cannot disagree.</summary>
        public MapCompletion CompletionOf(string mapId)
        {
            List<MapPoiDefinition> objectives = Atlas.ObjectivesOf(mapId);
            if (objectives.Count == 0)
                return new MapCompletion(0, 0, false);

            int completed = 0;
            for (int i = 0; i < objectives.Count; i++)
                if (Progress != null && Progress.PoiState(objectives[i].id) == DiscoveryState.Completed)
                    completed++;

            return new MapCompletion(completed, objectives.Count, true);
        }

        /// <summary>Evaluates a route to one map from the current map. Called only by the reachability filter,
        /// because planning is the expensive part of a query.</summary>
        public void EvaluateRoute(MapFacts facts, bool respectAvailability)
        {
            if (facts.RouteEvaluated)
                return;

            facts.RouteEvaluated = true;

            if (facts.IsCurrent)
            {
                facts.IsReachable = true;
                facts.RouteNote = "already here";
                return;
            }

            var options = new RouteOptions
            {
                Objective = RouteObjective.ShortestDistance,
                Heuristic = RouteHeuristic.None,
                RespectAvailability = respectAvailability
            };

            RoutePlan plan = RoutePlanner.Plan(Atlas, Progress, CurrentMapId, facts.Id, options);
            facts.IsReachable = plan.Found;
            facts.RouteNote = plan.Found ? plan.MapSequenceText : plan.FailureReason;
        }
    }

    // ======================================================================================
    //  Comparison vocabulary
    // ======================================================================================

    public enum NumericOp
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Between,

        /// <summary>Matches only when the datum is missing. This is how "unknown" is asked for explicitly.</summary>
        IsUnknown,

        /// <summary>Matches only when the datum exists.</summary>
        IsKnown
    }

    /// <summary>A serializable numeric test. An unknown datum never satisfies a positive comparison.</summary>
    [Serializable]
    public struct NumericComparison
    {
        public NumericOp op;
        public int value;
        public int secondValue;

        public static NumericComparison Any => new NumericComparison { op = NumericOp.IsKnown };

        public bool IsActive => op != NumericOp.Equal || value != 0 || secondValue != 0;

        public bool Matches(int datum, bool known)
        {
            if (!known)
                return op == NumericOp.IsUnknown;

            switch (op)
            {
                case NumericOp.Equal: return datum == value;
                case NumericOp.NotEqual: return datum != value;
                case NumericOp.Less: return datum < value;
                case NumericOp.LessOrEqual: return datum <= value;
                case NumericOp.Greater: return datum > value;
                case NumericOp.GreaterOrEqual: return datum >= value;
                case NumericOp.Between:
                    return datum >= Mathf.Min(value, secondValue) && datum <= Mathf.Max(value, secondValue);
                case NumericOp.IsUnknown: return false;
                default: return true;
            }
        }

        public string Describe(string noun)
        {
            switch (op)
            {
                case NumericOp.IsUnknown: return $"{noun} is not recorded";
                case NumericOp.IsKnown: return $"{noun} is recorded";
                case NumericOp.Between: return $"{noun} between {Mathf.Min(value, secondValue)}..{Mathf.Max(value, secondValue)}";
                case NumericOp.Equal: return $"{noun} = {value}";
                case NumericOp.NotEqual: return $"{noun} != {value}";
                case NumericOp.Less: return $"{noun} < {value}";
                case NumericOp.LessOrEqual: return $"{noun} <= {value}";
                case NumericOp.Greater: return $"{noun} > {value}";
                case NumericOp.GreaterOrEqual: return $"{noun} >= {value}";
                default: return noun;
            }
        }
    }

    /// <summary>How a tag list is matched.</summary>
    public enum TagOp
    {
        AnyOf,
        AllOf,
        NoneOf
    }

    /// <summary>Which fact an existence test asks about.</summary>
    public enum MapFlag
    {
        HasDungeon,
        HasTown,
        HasBoss,
        HasGuild,
        HasAltar,
        HasShop,
        HasStoryMarker,
        HasNpcPathAction,
        HasNpcRouteData,
        HasKnownNpcRoute,
        IsVisited,
        IsDiscovered,
        IsCurrent,
        IsFavorite,
        HasAnyChest,
        HasChestCommon,
        HasChestPurple,
        HasChestSpecial,
        HasUnknownDanger,
        HasKnownDanger,
        HasEnemyData,
        HasCompletionObjectives
    }

    /// <summary>A field whose absence can be asked about explicitly.</summary>
    public enum UnknownField
    {
        Danger,
        EnemyTypes,
        Chests,
        NpcRoute,
        RecommendedLevel
    }

    // ======================================================================================
    //  Predicates
    // ======================================================================================

    public enum FilterGroupOp
    {
        /// <summary>Every child must match (an empty AND matches everything).</summary>
        And,

        /// <summary>Any child may match (an empty OR matches nothing).</summary>
        Or,

        /// <summary>No child may match, which is the Exclude operator (an empty NOT matches everything).</summary>
        Not
    }

    /// <summary>One testable condition over a map's facts.</summary>
    public abstract class AtlasPredicate
    {
        public abstract bool Matches(MapFacts facts, AtlasQueryContext context);
        public abstract string Describe();
        public override string ToString() => Describe();
    }

    /// <summary>
    /// A nested group. Filters are groups of groups rather than a chain of conditionals, so
    /// "(Plains OR Highlands) AND HasDungeon AND Danger &lt;= 4" is expressed as data and evaluated by one
    /// recursive walk.
    /// </summary>
    public sealed class AtlasFilterGroup : AtlasPredicate
    {
        public FilterGroupOp Op;
        public readonly List<AtlasPredicate> Children = new List<AtlasPredicate>();

        public AtlasFilterGroup(FilterGroupOp op) => Op = op;

        public AtlasFilterGroup Add(AtlasPredicate child)
        {
            if (child != null)
                Children.Add(child);

            return this;
        }

        public bool IsEmpty => Children.Count == 0;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            switch (Op)
            {
                case FilterGroupOp.Or:
                    for (int i = 0; i < Children.Count; i++)
                        if (Children[i].Matches(facts, context))
                            return true;

                    return false;

                case FilterGroupOp.Not:
                    for (int i = 0; i < Children.Count; i++)
                        if (Children[i].Matches(facts, context))
                            return false;

                    return true;

                default:
                    for (int i = 0; i < Children.Count; i++)
                        if (!Children[i].Matches(facts, context))
                            return false;

                    return true;
            }
        }

        public override string Describe()
        {
            if (Children.Count == 0)
                return Op == FilterGroupOp.Or ? "(nothing)" : "any map";

            var parts = new List<string>();
            for (int i = 0; i < Children.Count; i++)
                parts.Add(Children[i].Describe());

            if (Children.Count == 1 && Op != FilterGroupOp.Not)
                return parts[0];

            string joiner = Op == FilterGroupOp.And ? " AND " : Op == FilterGroupOp.Or ? " OR " : " NOT ";
            string body = string.Join(joiner, parts);
            return Op == FilterGroupOp.Not ? "NOT (" + body + ")" : "(" + body + ")";
        }
    }

    /// <summary>Text search over name, aliases and id.</summary>
    public sealed class TextPredicate : AtlasPredicate
    {
        private readonly string text;

        public TextPredicate(string text) => this.text = text ?? string.Empty;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            if (string.IsNullOrWhiteSpace(text) || facts.Map == null)
                return true;

            return Contains(facts.Map.displayName) || Contains(facts.Map.id) ||
                   AnyOf(facts.Map.aliases) || Contains(facts.Map.locationId);
        }

        private bool Contains(string value) =>
            !string.IsNullOrEmpty(value) && value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        private bool AnyOf(string[] values)
        {
            if (values == null)
                return false;

            for (int i = 0; i < values.Length; i++)
                if (Contains(values[i]))
                    return true;

            return false;
        }

        public override string Describe() => string.IsNullOrWhiteSpace(text) ? "any name" : $"name contains '{text}'";
    }

    public sealed class LocationKindPredicate : AtlasPredicate
    {
        private readonly LocationKind[] kinds;

        public LocationKindPredicate(params LocationKind[] kinds) => this.kinds = kinds ?? Array.Empty<LocationKind>();

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            if (kinds.Length == 0)
                return true;

            AtlasLocationDefinition location = context.Atlas.GetLocation(facts.Map != null ? facts.Map.locationId : null);
            if (location == null)
                return false;

            for (int i = 0; i < kinds.Length; i++)
                if (location.kind == kinds[i])
                    return true;

            return false;
        }

        public override string Describe() => "location type is " + string.Join(" or ", kinds);
    }

    public sealed class ParentLocationPredicate : AtlasPredicate
    {
        private readonly string locationId;

        public ParentLocationPredicate(string locationId) => this.locationId = locationId ?? string.Empty;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            if (string.IsNullOrEmpty(locationId))
                return true;

            // Matches the map's own location or any ancestor, so "Nation = X" also finds its regions.
            string current = facts.Map != null ? facts.Map.locationId : string.Empty;
            int guard = 0;
            while (!string.IsNullOrEmpty(current) && guard++ < 32)
            {
                if (string.Equals(current, locationId, StringComparison.Ordinal))
                    return true;

                AtlasLocationDefinition location = context.Atlas.GetLocation(current);
                if (location == null)
                    return false;

                current = location.parentId;
            }

            return false;
        }

        public override string Describe() => $"within '{locationId}'";
    }

    public sealed class TagPredicate : AtlasPredicate
    {
        private readonly string[] tags;
        private readonly TagOp op;

        public TagPredicate(TagOp op, params string[] tags)
        {
            this.op = op;
            this.tags = tags ?? Array.Empty<string>();
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            if (tags.Length == 0)
                return true;

            int matched = 0;
            for (int i = 0; i < tags.Length; i++)
                if (facts.Tags.Contains(tags[i]))
                    matched++;

            switch (op)
            {
                case TagOp.AnyOf: return matched > 0;
                case TagOp.AllOf: return matched == tags.Length;
                case TagOp.NoneOf: return matched == 0;
                default: return true;
            }
        }

        public override string Describe()
        {
            string joiner = op == TagOp.AnyOf ? " or " : op == TagOp.AllOf ? " and " : ", not ";
            string prefix = op == TagOp.NoneOf ? "none of " : string.Empty;
            return "biome/tag is " + prefix + string.Join(joiner, tags);
        }
    }

    public sealed class FlagPredicate : AtlasPredicate
    {
        private readonly MapFlag flag;
        private readonly bool expected;

        public FlagPredicate(MapFlag flag, bool expected = true)
        {
            this.flag = flag;
            this.expected = expected;
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context) => Value(facts) == expected;

        public static bool Value(MapFacts facts, MapFlag flag)
        {
            switch (flag)
            {
                case MapFlag.HasDungeon: return facts.HasDungeon;
                case MapFlag.HasTown: return facts.HasTown;
                case MapFlag.HasBoss: return facts.HasBoss;
                case MapFlag.HasGuild: return facts.HasGuild;
                case MapFlag.HasAltar: return facts.HasAltar;
                case MapFlag.HasShop: return facts.HasShop;
                case MapFlag.HasStoryMarker: return facts.HasStoryMarker;
                case MapFlag.HasNpcPathAction: return facts.HasNpcPathAction;
                case MapFlag.HasNpcRouteData: return facts.HasNpcRouteData;
                case MapFlag.HasKnownNpcRoute: return facts.HasKnownNpcRoute;
                case MapFlag.IsVisited: return facts.IsVisited;
                case MapFlag.IsDiscovered: return facts.IsDiscovered;
                case MapFlag.IsCurrent: return facts.IsCurrent;
                case MapFlag.IsFavorite: return facts.IsFavorite;
                case MapFlag.HasAnyChest: return !facts.Chests.IsEmpty;
                case MapFlag.HasChestCommon: return facts.Chests.Common > 0;
                case MapFlag.HasChestPurple: return facts.Chests.Purple > 0;
                case MapFlag.HasChestSpecial: return facts.Chests.Special > 0;
                case MapFlag.HasUnknownDanger: return !facts.Danger.IsKnown;
                case MapFlag.HasKnownDanger: return facts.Danger.IsKnown;
                case MapFlag.HasEnemyData: return facts.EnemySpecies.Count > 0;
                case MapFlag.HasCompletionObjectives: return facts.Completion.IsKnown && facts.Completion.Eligible > 0;
                default: return false;
            }
        }

        private bool Value(MapFacts facts) => Value(facts, flag);

        public override string Describe() => (expected ? string.Empty : "not ") + flag;
    }

    public sealed class ChestCountPredicate : AtlasPredicate
    {
        private readonly ChestTier tier;
        private readonly NumericComparison comparison;

        public ChestCountPredicate(ChestTier tier, NumericComparison comparison)
        {
            this.tier = tier;
            this.comparison = comparison;
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            int count;
            switch (tier)
            {
                case ChestTier.Common: count = facts.Chests.Common; break;
                case ChestTier.Purple: count = facts.Chests.Purple; break;
                case ChestTier.Special: count = facts.Chests.Special; break;
                default: count = facts.Chests.Total; break;
            }

            return comparison.Matches(count, facts.Map != null);
        }

        public override string Describe() => comparison.Describe($"{tier} chest count");
    }

    public sealed class DangerPredicate : AtlasPredicate
    {
        private readonly NumericComparison comparison;

        public DangerPredicate(NumericComparison comparison) => this.comparison = comparison;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            // A map with no recorded danger is matched against the range [0,0] but only counts as "known" when
            // the atlas actually authored a rating - so an unknown map cannot slip through "< 5".
            int value = facts.Danger.IsKnown ? facts.Danger.Max : 0;
            return comparison.Matches(value, facts.Danger.IsKnown);
        }

        public override string Describe() => comparison.Describe("danger level");
    }

    public sealed class EnemySpeciesPredicate : AtlasPredicate
    {
        private readonly string species;

        public EnemySpeciesPredicate(string species) => this.species = species ?? string.Empty;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            if (string.IsNullOrEmpty(species))
                return true;

            for (int i = 0; i < facts.EnemySpecies.Count; i++)
                if (string.Equals(facts.EnemySpecies[i], species, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        public override string Describe() => $"enemy species is '{species}'";
    }

    public sealed class StoryMarkerPredicate : AtlasPredicate
    {
        private readonly PoiKind[] kinds;

        public StoryMarkerPredicate(params PoiKind[] kinds)
        {
            this.kinds = kinds == null || kinds.Length == 0
                ? new[] { PoiKind.MainStory, PoiKind.SideStory, PoiKind.CrossedPath }
                : kinds;
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            for (int i = 0; i < facts.StoryMarkers.Count; i++)
                for (int k = 0; k < kinds.Length; k++)
                    if (facts.StoryMarkers[i].kind == kinds[k])
                        return true;

            return false;
        }

        public override string Describe() => "has story marker: " + string.Join("/", kinds);
    }

    public sealed class DiscoveryPredicate : AtlasPredicate
    {
        private readonly DiscoveryState minimum;
        private readonly bool requireExact;

        public DiscoveryPredicate(DiscoveryState state, bool exact = false)
        {
            minimum = state;
            requireExact = exact;
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            DiscoveryState current = facts.IsVisited
                ? DiscoveryState.Completed
                : facts.IsDiscovered
                    ? DiscoveryState.Discovered
                    : DiscoveryState.Undiscovered;

            return requireExact ? current == minimum : current >= minimum;
        }

        public override string Describe() => requireExact ? $"discovery is exactly {minimum}" : $"discovery at least {minimum}";
    }

    public sealed class ReachablePredicate : AtlasPredicate
    {
        private readonly bool respectAvailability;

        public ReachablePredicate(bool respectAvailability = true) => this.respectAvailability = respectAvailability;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            context.EvaluateRoute(facts, respectAvailability);
            return facts.IsReachable;
        }

        public override string Describe() => respectAvailability
            ? "reachable from the current map"
            : "connected to the current map when locks are ignored";
    }

    public sealed class NpcRoutePredicate : AtlasPredicate
    {
        private readonly bool requireKnown;
        private readonly bool requireData;

        public NpcRoutePredicate(bool requireData, bool requireKnown)
        {
            this.requireData = requireData;
            this.requireKnown = requireKnown;
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context) =>
            (!requireData || facts.HasNpcRouteData) && (!requireKnown || facts.HasKnownNpcRoute);

        public override string Describe() => requireKnown
            ? "has a known NPC route"
            : "has authored NPC route data";
    }

    public sealed class UnknownFieldPredicate : AtlasPredicate
    {
        private readonly UnknownField field;
        private readonly bool mustBeUnknown;

        public UnknownFieldPredicate(UnknownField field, bool mustBeUnknown = true)
        {
            this.field = field;
            this.mustBeUnknown = mustBeUnknown;
        }

        public override bool Matches(MapFacts facts, AtlasQueryContext context) => IsUnknown(facts, field) == mustBeUnknown;

        private static bool IsUnknown(MapFacts facts, UnknownField field)
        {
            switch (field)
            {
                case UnknownField.Danger: return !facts.Danger.IsKnown;
                case UnknownField.EnemyTypes: return facts.EnemySpecies.Count == 0;
                case UnknownField.Chests: return facts.Chests.IsEmpty;
                case UnknownField.NpcRoute: return !facts.HasNpcRouteData;
                default: return true;
            }
        }

        public override string Describe() => $"{field} is " + (mustBeUnknown ? "not recorded" : "recorded");
    }

    // ======================================================================================
    //  Query and facet builder
    // ======================================================================================

    public sealed class AtlasQueryResult
    {
        public MapFacts Facts;
        public string MapId => Facts != null ? Facts.Id : string.Empty;
        public string Name => Facts != null ? Facts.DisplayName : string.Empty;
        public string Breadcrumb => Facts != null ? Facts.Breadcrumb : string.Empty;
        public string Summary => Facts != null ? Facts.Summary : string.Empty;
    }

    public sealed class AtlasQueryResults
    {
        public readonly List<AtlasQueryResult> Items = new List<AtlasQueryResult>();
        public string Description = string.Empty;

        public int Count => Items.Count;

        public string ResultCountText => Count == 1 ? "1 map" : $"{Count} maps";
    }

    /// <summary>
    /// Facet selections plus an optional advanced group. Facets are deliberately combined the way a player
    /// expects: values inside one facet are OR-ed (Plains OR Forest) and different facets are AND-ed, while
    /// <see cref="Advanced"/> (a full nested group, including NOT) is AND-ed with the rest so an advanced rule
    /// can only narrow a search.
    /// </summary>
    public sealed class AtlasQueryBuilder
    {
        public string Text = string.Empty;
        public readonly List<string> BiomeTags = new List<string>();
        public readonly List<LocationKind> LocationKinds = new List<LocationKind>();
        public string WithinLocationId = string.Empty;
        public readonly List<MapFlag> RequiredFlags = new List<MapFlag>();
        public readonly List<MapFlag> ExcludedFlags = new List<MapFlag>();
        public readonly List<ChestTier> ChestTiers = new List<ChestTier>();
        public NumericComparison Danger;
        public NumericComparison RecommendedLevel;
        public string EnemySpecies = string.Empty;
        public bool StoryMarkersOnly;
        public bool VisitedOnly;
        public bool UndiscoveredOnly;
        public bool RouteReachableOnly;
        public bool ReachableIgnoringLocks;
        public bool NpcRouteDataOnly;
        public bool NpcRouteKnownOnly;
        public bool IncludeUnknownDanger;
        public bool PendingCompletionOnly;
        public AtlasFilterGroup Advanced;

        /// <summary>True when any facet is set, so the UI can show whether a search is filtered.</summary>
        public bool HasAnyFilter =>
            !string.IsNullOrWhiteSpace(Text) || BiomeTags.Count > 0 || LocationKinds.Count > 0 ||
            !string.IsNullOrEmpty(WithinLocationId) || RequiredFlags.Count > 0 || ExcludedFlags.Count > 0 ||
            ChestTiers.Count > 0 || Danger.op != NumericOp.Equal || Danger.value != 0 || Danger.secondValue != 0 ||
            RecommendedLevel.op != NumericOp.Equal || RecommendedLevel.value != 0 || RecommendedLevel.secondValue != 0 ||
            !string.IsNullOrEmpty(EnemySpecies) || StoryMarkersOnly || VisitedOnly || UndiscoveredOnly ||
            RouteReachableOnly || NpcRouteDataOnly || NpcRouteKnownOnly || IncludeUnknownDanger ||
            PendingCompletionOnly || (Advanced != null && !Advanced.IsEmpty);

        public void Reset()
        {
            Text = string.Empty;
            BiomeTags.Clear();
            LocationKinds.Clear();
            WithinLocationId = string.Empty;
            RequiredFlags.Clear();
            ExcludedFlags.Clear();
            ChestTiers.Clear();
            Danger = default;
            RecommendedLevel = default;
            EnemySpecies = string.Empty;
            StoryMarkersOnly = false;
            VisitedOnly = false;
            UndiscoveredOnly = false;
            RouteReachableOnly = false;
            ReachableIgnoringLocks = false;
            NpcRouteDataOnly = false;
            NpcRouteKnownOnly = false;
            IncludeUnknownDanger = false;
            PendingCompletionOnly = false;
            Advanced = null;
        }

        /// <summary>Builds the predicate tree. The root is an AND group, which is what makes the facets combine
        /// with AND and each facet's own values combine with OR.</summary>
        public AtlasPredicate Build()
        {
            var root = new AtlasFilterGroup(FilterGroupOp.And);

            if (!string.IsNullOrWhiteSpace(Text))
                root.Add(new TextPredicate(Text));

            if (LocationKinds.Count > 0)
                root.Add(new LocationKindPredicate(LocationKinds.ToArray()));

            if (!string.IsNullOrEmpty(WithinLocationId))
                root.Add(new ParentLocationPredicate(WithinLocationId));

            if (BiomeTags.Count > 0)
                root.Add(new TagPredicate(TagOp.AnyOf, BiomeTags.ToArray()));

            for (int i = 0; i < RequiredFlags.Count; i++)
                root.Add(new FlagPredicate(RequiredFlags[i], true));

            for (int i = 0; i < ExcludedFlags.Count; i++)
                root.Add(new FlagPredicate(ExcludedFlags[i], false));

            for (int i = 0; i < ChestTiers.Count; i++)
                root.Add(new ChestCountPredicate(ChestTiers[i], new NumericComparison
                {
                    op = NumericOp.Greater,
                    value = 0
                }));

            if (Danger.op != NumericOp.Equal || Danger.value != 0 || Danger.secondValue != 0)
                root.Add(new DangerPredicate(Danger));

            if (RecommendedLevel.op != NumericOp.Equal || RecommendedLevel.value != 0 || RecommendedLevel.secondValue != 0)
                root.Add(new RecommendedLevelPredicate(RecommendedLevel));

            if (!string.IsNullOrEmpty(EnemySpecies))
                root.Add(new EnemySpeciesPredicate(EnemySpecies));

            if (StoryMarkersOnly)
                root.Add(new StoryMarkerPredicate());

            if (VisitedOnly)
                root.Add(new DiscoveryPredicate(DiscoveryState.Completed));

            if (UndiscoveredOnly)
                root.Add(new DiscoveryPredicate(DiscoveryState.Undiscovered, true));

            if (RouteReachableOnly)
                root.Add(new ReachablePredicate(!ReachableIgnoringLocks));

            if (NpcRouteDataOnly || NpcRouteKnownOnly)
                root.Add(new NpcRoutePredicate(NpcRouteDataOnly, NpcRouteKnownOnly));

            if (IncludeUnknownDanger)
                root.Add(new UnknownFieldPredicate(UnknownField.Danger, true));

            if (PendingCompletionOnly)
                root.Add(new PendingCompletionPredicate());

            if (Advanced != null && !Advanced.IsEmpty)
                root.Add(Advanced);

            return root;
        }

        /// <summary>Runs the built predicate over every map in the atlas, in atlas order.</summary>
        public AtlasQueryResults Evaluate(AtlasQueryContext context)
        {
            var results = new AtlasQueryResults();
            if (context == null || context.Atlas == null)
                return results;

            AtlasPredicate predicate = Build();
            results.Description = predicate.Describe();

            if (context.Atlas.maps == null)
                return results;

            for (int i = 0; i < context.Atlas.maps.Length; i++)
            {
                MapDefinition map = context.Atlas.maps[i];
                if (map == null)
                    continue;

                MapFacts facts = context.Describe(map.id);
                if (!predicate.Matches(facts, context))
                    continue;

                results.Items.Add(new AtlasQueryResult { Facts = facts });
            }

            return results;
        }

        /// <summary>Copies the settings that describe a query, for the result header.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Text))
                sb.Append("name~'").Append(Text).Append("' ");

            if (BiomeTags.Count > 0)
                sb.Append("tag(").Append(string.Join(" OR ", BiomeTags)).Append(") ");

            for (int i = 0; i < RequiredFlags.Count; i++)
                sb.Append("+").Append(RequiredFlags[i]).Append(' ');

            for (int i = 0; i < ExcludedFlags.Count; i++)
                sb.Append("-").Append(ExcludedFlags[i]).Append(' ');

            if (Danger.op != NumericOp.Equal || Danger.value != 0 || Danger.secondValue != 0)
                sb.Append(Danger.Describe("danger")).Append(' ');

            if (RouteReachableOnly)
                sb.Append(ReachableIgnoringLocks ? "connected(ignoring locks) " : "reachable ");

            return sb.Length == 0 ? "no filters" : sb.ToString().Trim();
        }
    }

    /// <summary>A map that still has unauthored-as-complete objectives. Maps with no objectives never match.</summary>
    public sealed class PendingCompletionPredicate : AtlasPredicate
    {
        public override bool Matches(MapFacts facts, AtlasQueryContext context) =>
            facts.Completion.IsKnown && facts.Completion.Eligible > 0 && facts.Completion.Completed < facts.Completion.Eligible;

        public override string Describe() => "has unfinished objectives";
    }

    public sealed class RecommendedLevelPredicate : AtlasPredicate
    {
        private readonly NumericComparison comparison;

        public RecommendedLevelPredicate(NumericComparison comparison) => this.comparison = comparison;

        public override bool Matches(MapFacts facts, AtlasQueryContext context)
        {
            int level = facts.Map != null ? facts.Map.recommendedLevel : 0;
            return comparison.Matches(level, level > 0);
        }

        public override string Describe() => comparison.Describe("recommended level");
    }
}
