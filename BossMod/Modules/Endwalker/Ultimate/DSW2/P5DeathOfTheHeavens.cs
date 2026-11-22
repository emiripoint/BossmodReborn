namespace BossMod.Endwalker.Ultimate.DSW2;

sealed class P5DeathOfTheHeavensHeavyImpact(BossModule module) : HeavyImpact(module, 10.5d);

sealed class P5DeathOfTheHeavensGaze(BossModule module) : DragonsGaze(module, (uint)OID.BossP5, 23.5d);

// TODO: make more meaningful somehow
sealed class P5DeathOfTheHeavensDooms(BossModule module) : BossComponent(module)
{
    public BitMask Dooms;

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (Dooms[slot])
            hints.Add("Doom", false);
    }

    // note: we could also use status, but it appears slightly later
    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.Deathstorm)
            foreach (var t in spell.Targets)
                Dooms[Raid.FindSlot(t.ID)] = true;
    }
}

sealed class P5DeathOfTheHeavensLightningStorm : Components.UniformStackSpread
{
    public P5DeathOfTheHeavensLightningStorm(BossModule module) : base(module, default, 5f)
    {
        AddSpreads(Raid.WithoutSlot(true, true, true));
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.LightningStormAOE)
            Spreads.Clear();
    }
}

sealed class P5DeathOfTheHeavensHeavensflame(BossModule module) : Components.GenericKnockback(module, (uint)AID.HeavensflameAOE)
{
    public bool KnockbackDone;
    private readonly DSW2Config _config = Service.Config.Get<DSW2Config>();
    private readonly WPos[] _playerAdjustedPositions = new WPos[PartyState.MaxPartySize];
    private readonly int[] _playerIcons = new int[PartyState.MaxPartySize]; // 0 = unassigned, 1 = circle/red, 2 = triangle/green, 3 = cross/blue, 4 = square/purple
    private BitMask _brokenTethers;
    private BitMask _dooms;
    private readonly List<WPos> _cleanses = [];
    private WDir _relSouth; // TODO: this is quite hacky, works for LPDU...
    private readonly WDir[] _naurDoomDirections = new WDir[PartyState.MaxPartySize];

    private const float _knockbackDistance = 16f;
    private const float _aoeRadius = 10f;
    private const float _tetherBreakDistance = 32f; // TODO: verify...

    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
    {
        return new Knockback[1] { new(Arena.Center, _knockbackDistance) };
    }

    public override void Update()
    {
        foreach (var (slot, player) in Raid.WithSlot(false, true, true))
            _playerAdjustedPositions[slot] = !KnockbackDone ? AwayFromSource(player.Position, Arena.Center, _knockbackDistance) : player.Position;
    }

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (_playerIcons[slot] == 0)
            return;

        if (!KnockbackDone && IsImmune(slot, WorldState.CurrentTime))
            hints.Add("Cancel knockback immunity!");

        var actorAdjPos = _playerAdjustedPositions[slot];
        if (!Arena.InBounds(actorAdjPos))
            hints.Add("About to be knocked into wall!");

        if (Raid.WithSlot(false, true, true).Exclude(actor).WhereSlot(s => _playerAdjustedPositions[s].InCircle(actorAdjPos, _aoeRadius)).Any())
            hints.Add("Spread!");

        var partner = FindTetheredPartner(slot);
        if (partner >= 0 && _playerAdjustedPositions[partner].InCircle(actorAdjPos, _tetherBreakDistance))
            hints.Add("Aim to break tether!");
    }

    public override PlayerPriority CalcPriority(int pcSlot, Actor pc, int playerSlot, Actor player, ref uint customColor)
    {
        return _playerIcons[pcSlot] == 0 ? PlayerPriority.Irrelevant :
            !_brokenTethers[pcSlot] && _playerIcons[pcSlot] == _playerIcons[playerSlot] ? PlayerPriority.Interesting
            : PlayerPriority.Normal;
    }

    public override void DrawArenaForeground(int pcSlot, Actor pc)
    {
        if (_playerIcons[pcSlot] == 0)
            return;

        foreach (var hint in PositionHints(pcSlot))
            Arena.ZoneCircleOutline(hint, 1, Colors.Safe);

        var partner = FindTetheredPartner(pcSlot);
        if (partner >= 0)
            Arena.AddLine(pc.Position, Raid[partner]!.Position, Colors.Safe);

        DrawKnockback(pc, _playerAdjustedPositions[pcSlot], Arena);

        foreach (var (slot, _) in Raid.WithSlot(false, true, true).Exclude(pc))
            Arena.ZoneCircleOutline(_playerAdjustedPositions[slot], _aoeRadius, Colors.Danger);
    }

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == (uint)AID.WingsOfSalvationAOE)
        {
            _cleanses.Add(spell.LocXZ);
            _relSouth += spell.LocXZ - Arena.Center;
        }
    }

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        base.OnEventCast(caster, spell);
        if (spell.Action.ID == (uint)AID.FaithUnmoving)
            KnockbackDone = true;
    }

    public override void OnStatusGain(Actor actor, ref ActorStatus status)
    {
        if (status.ID == (uint)SID.Doom)
        {
            var slot = Raid.FindSlot(actor.InstanceID);
            if (slot is >= 0 and < PartyState.MaxPartySize)
            {
                _dooms[slot] = true;
                if (_playerIcons.Any(i => i != 0))
                    CaptureNaurAnchors();
            }
        }
    }

    public override void OnUntethered(Actor source, in ActorTetherInfo tether)
    {
        _brokenTethers[Raid.FindSlot(source.InstanceID)] = true;
        _brokenTethers[Raid.FindSlot(tether.Target)] = true;
    }

    public override void OnEventIcon(Actor actor, uint iconID, ulong targetID)
    {
        var icon = iconID switch
        {
            (uint)IconID.HeavensflameCircle => 1,
            (uint)IconID.HeavensflameTriangle => 2,
            (uint)IconID.HeavensflameCross => 3,
            (uint)IconID.HeavensflameSquare => 4,
            _ => 0
        };
        if (icon != 0)
        {
            var slot = Raid.FindSlot(actor.InstanceID);
            if (slot is >= 0 and < PartyState.MaxPartySize)
            {
                _playerIcons[slot] = icon;
                CaptureNaurAnchors();
            }
        }
    }

    private void CaptureNaurAnchors()
    {
        if (_config.P5HeavensflamePositions != DSW2Config.P5HeavensflameStrategy.NAUR)
            return;
        // Capture the planted sides as icons appear, before non-dooms adjust.
        for (var i = 0; i < PartyState.MaxPartySize; ++i)
        {
            if (_dooms[i] && _naurDoomDirections[i] == default && Raid[i] is { IsDeadOrDestroyed: false } player)
            {
                var offset = player.Position - Arena.Center;
                if (offset.LengthSq() > 1f)
                    _naurDoomDirections[i] = offset.Normalized();
            }
        }
    }

    private int FindTetheredPartner(int slot)
    {
        if (_brokenTethers[slot])
            return -1;
        if (_playerIcons[slot] == 0)
            return -1;
        for (var i = 0; i < _playerIcons.Length; ++i)
            if (i != slot && _playerIcons[i] == _playerIcons[slot])
                return i;
        return -1;
    }

    private List<WPos> PositionHints(int slot)
    {
        var icon = _playerIcons[slot];
        if (icon == 0)
            return [];
        if (_config.P5HeavensflamePositions == DSW2Config.P5HeavensflameStrategy.NAUR)
            return NaurPositionHints(slot);
        // LPDU: circles on E/W cleanses, triangles on SE/NW, crosses on N/S, squares on SW/NE.
        var center = Arena.Center;
        var angle = Angle.FromDirection(_relSouth) + 135f.Degrees() - icon * 45f.Degrees();
        var offset = _tetherBreakDistance * 0.5f * angle.ToDirection();
        var hints = new List<WPos>(2);
        switch (icon)
        {
            case 1: // circle - show two cleanses closest to E and W
                hints.Add(ClosestCleanse(center + offset));
                hints.Add(ClosestCleanse(center - offset));
                break;
            case 2: // triangle/square - doom to closest cleanse to SE/SW, otherwise opposite
            case 4:
                var cleanseSpot = ClosestCleanse(center + offset);
                hints.Add(_dooms[slot] ? cleanseSpot : center - (cleanseSpot - center));
                break;
            case 3: // cross - show two spots to N and S
                hints.Add(center + offset);
                hints.Add(center - offset);
                break;
        }
        return hints;
    }

    private List<WPos> NaurPositionHints(int slot)
    {
        var cleanses = _cleanses.Distinct().ToArray();
        if (cleanses.Length > 4)
            return [];
        var anchorSlot = _dooms[slot] ? slot : FindTetheredPartner(slot);
        if (anchorSlot >= 0 && _dooms[anchorSlot])
        {
            if (NaurCleanse(anchorSlot, cleanses) is not { } cleanse)
                return [];
            // Two dooms claiming one puddle is ambiguous; do not direct either pair there.
            for (var i = 0; i < PartyState.MaxPartySize; ++i)
                if (i != anchorSlot && _dooms[i] && NaurCleanse(i, cleanses) == cleanse)
                    return [];
            return [_dooms[slot] ? cleanse : Arena.Center - (cleanse - Arena.Center)];
        }
        if (_playerIcons[slot] == 3 && cleanses.Length == 4)
        {
            WDir south = default;
            foreach (var cleanse in cleanses)
                south += cleanse - Arena.Center;
            if (south.LengthSq() > 1f)
            {
                var offset = _tetherBreakDistance * 0.5f * south.Normalized();
                return [Arena.Center + offset, Arena.Center - offset];
            }
        }
        return [];
    }

    private WPos? NaurCleanse(int slot, WPos[] cleanses)
    {
        var direction = _naurDoomDirections[slot];
        WPos? result = null;
        var minAlignment = 30f.Degrees().Cos();
        foreach (var cleanse in cleanses)
        {
            var alignment = direction.Dot((cleanse - Arena.Center).Normalized());
            if (alignment > minAlignment)
            {
                if (result != null)
                    return null;
                result = cleanse;
            }
        }
        return result;
    }

    private WPos ClosestCleanse(WPos p) => _cleanses.MinBy(c => (c - p).LengthSq());
}

sealed class P5DeathOfTheHeavensMeteorCircle(BossModule module) : Components.Adds(module, (uint)OID.MeteorCircle);
