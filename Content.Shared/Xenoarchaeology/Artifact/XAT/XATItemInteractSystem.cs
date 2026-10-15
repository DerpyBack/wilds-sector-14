using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Kitchen;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Content.Shared.Whitelist;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared.Xenoarchaeology.Artifact.XAT;

public sealed partial class XATItemInteractSystem : BaseXATSystem<XATItemInteractComponent>
{
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private SharedDoAfterSystem _doafter = default!;
    [Dependency] private SharedStackSystem _stack = default!;
    [Dependency] private TagSystem _tag = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IPrototypeManager _prototype = default!;

    public override void Initialize()
    {
        base.Initialize();

        XATSubscribeDirectEvent<InteractUsingEvent>(OnInteractUsing);
        XATSubscribeDirectEvent<XATItemInteractDoAfterEvent>(OnDoAfter);
    }

    private void OnInteractUsing(Entity<XenoArtifactComponent> artifact, Entity<XATItemInteractComponent, XenoArtifactNodeComponent> node, ref InteractUsingEvent args)
    {
        var whitelistPass = _whitelist.IsWhitelistPass(node.Comp1.Whitelist, args.Used);
        var recipePass = PassRecipeResult(node.Comp1, args.Used);
        var canModifyStack = CanModifyStack(node.Comp1, args.Used);
        //Log.Debug($"[xat-item-interact] OnInteractUsing item={ToPrettyString(args.Used)} whitelistPass={whitelistPass} recipePass={recipePass} handled={args.Handled} canModifyStack={canModifyStack}");

        if (!whitelistPass || !recipePass || args.Handled || !canModifyStack)
            return;

        if (node.Comp1.UseTime <= TimeSpan.Zero)
        {
            ModifyStack(node.Comp1, args.Used);
            Trigger(artifact, node);
            args.Handled = true;
            return;
        }

        var doAfterArgs = new DoAfterArgs(
            EntityManager,
            args.User,
            node.Comp1.UseTime,
            new XATItemInteractDoAfterEvent(GetNetEntity(node)),
            artifact,
            target: artifact,
            used: args.Used
        )
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
        };

        if (!_doafter.TryStartDoAfter(doAfterArgs))
            return;
        args.Handled = true;
    }

    private void OnDoAfter(Entity<XenoArtifactComponent> artifact, Entity<XATItemInteractComponent, XenoArtifactNodeComponent> node, ref XATItemInteractDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;
        if (GetEntity(args.Node) != node.Owner)
            return;

        if (args.Used is not { } used)
            return;

        if (TerminatingOrDeleted(used))
            return;

        var whitelistPass = _whitelist.IsWhitelistPass(node.Comp1.Whitelist, used);
        var recipePass = PassRecipeResult(node.Comp1, used);
        //Log.Debug($"[xat-item-interact] OnDoAfter item={ToPrettyString(used)} whitelistPass={whitelistPass} recipePass={recipePass}");

        if (!whitelistPass || !recipePass)
            return;

        ModifyStack(node.Comp1, used);
        Trigger(artifact, node);
        args.Handled = true;
    }

    private bool PassRecipeResult(XATItemInteractComponent xatComponent, EntityUid item)
    {
        if (!xatComponent.RequireRecipeResult)
            return true;

        if (MetaData(item).EntityPrototype is not { } proto)
        {
            //Log.Debug($"[xat-item-interact] PassRecipeResult: {ToPrettyString(item)} has no EntityPrototype, failing");
            return false;
        }

        // Unconditional exclusion, checked before anything else: covers both raw ingredients the
        // "never used elsewhere" heuristic below would otherwise misclassify as finished, and items
        // that are technically a recipe result but aren't a meal on their own (e.g. pastry dough,
        // a plain cake base, an empty taco shell).
        if (_tag.HasTag(item, "IgnoredByArtifactTrigger"))
        {
            //Log.Debug($"[xat-item-interact] PassRecipeResult: {proto.ID} FAILED - excluded via IgnoredByArtifactTrigger");
            return false;
        }

        // Empty food packets/wrappers are edible (often with a joke poison effect) but are never a
        // recipe ingredient, so the "never used elsewhere" heuristic below would otherwise treat
        // litter as a finished dish.
        if (_tag.HasTag(item, "Trash"))
        {
            //Log.Debug($"[xat-item-interact] PassRecipeResult: {proto.ID} FAILED - tagged Trash");
            return false;
        }

        // Items cooked via a grill/construction-graph temperature step (steaks, cutlets, bacon,
        // patties) never go through a microwaveMealRecipe, but are tagged Cooked directly.
        if (_tag.HasTag(item, "Cooked"))
        {
            //Log.Debug($"[xat-item-interact] PassRecipeResult: {proto.ID} PASSED - tagged Cooked");
            return true;
        }

        var isResult = false;
        var isIngredient = false;
        foreach (var recipe in _prototype.EnumeratePrototypes<FoodRecipePrototype>())
        {
            if (recipe.Result == proto.ID)
                isResult = true;
            if (recipe.IngredientsSolids.ContainsKey(proto.ID))
                isIngredient = true;
        }

        if (isResult)
        {
            //Log.Debug($"[xat-item-interact] PassRecipeResult: {proto.ID} PASSED - is a recipe result");
            return true;
        }

        // Not explicitly tagged Cooked and not a known recipe's result - treat it as a finished,
        // standalone dish (a whole pizza, a baked bun) if it's never itself used as an ingredient
        // in another recipe.
        if (!isIngredient)
        {
            //Log.Debug($"[xat-item-interact] PassRecipeResult: {proto.ID} PASSED - never used as a recipe ingredient");
            return true;
        }

        //Log.Debug($"[xat-item-interact] PassRecipeResult: {proto.ID} FAILED - not Cooked, not a recipe result, is used as an ingredient elsewhere");
        return false;
    }

    private bool CanModifyStack(XATItemInteractComponent xatComponent, EntityUid item)
    {
        if (xatComponent.ReduceStackBy <= 0)
            return true;

        if (!TryComp<StackComponent>(item, out var stack))
            return true;

        return stack.Unlimited || stack.Count >= xatComponent.ReduceStackBy;
    }

    private void ModifyStack(XATItemInteractComponent xatComponent, EntityUid item)
    {
        if (xatComponent.ReduceStackBy <= 0) return;

        if (!TryComp<StackComponent>(item, out var stack))
        {
            if (_net.IsServer)
                QueueDel(item);
            return;
        }

        if (stack.Unlimited) return;

        if (stack.Count < xatComponent.ReduceStackBy) return;

        _stack.TryUse((item, stack), xatComponent.ReduceStackBy);
    }
}
