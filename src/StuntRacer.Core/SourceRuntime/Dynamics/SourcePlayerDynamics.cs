namespace StuntRacer.Core.SourceRuntime;

/// <summary>
/// Player-only reference-derived integer kernel. One Step is one unretuned movement
/// update, with no clock, render interpolation, opponents, drafting or stock claim.
/// </summary>
internal sealed class SourcePlayerDynamics
{
    private readonly SourceAmigaPackage _package;
    private readonly SourceOrientation _orientation;
    private readonly int[] _inclinationCosine;
    private readonly Contact[] _contacts = [new(), new(), new()];
    private SourcePlayerControlState _control;
    private SourceVector _velocity;
    private SourceVector _bodyVelocity;
    private SourceVector _worldAcceleration;
    private SourceVector _angularVelocity;
    private SourceVector _angularAcceleration;
    private int _x;
    private int _y;
    private int _z;
    private ushort _yaw;
    private ushort _pitch;
    private ushort _roll;
    private long _tick;
    private bool _touching;
    private bool _offMap;
    private int _offRoadTicks;
    private int _rearOffRoadDistance;
    private int _section;
    private int _segment;
    private int _force;
    private int _damage;
    private int _damagedCount;
    private int _damageHoles;
    private int _smashCountdown;
    private int _groundedDelay;
    private int _recoveries;
    private int _sideFlags;
    private bool _smallerLimitRequired;
    private int _damageValue;
    private bool _damagedThisTick;

    public SourcePlayerDynamics(SourceAmigaPackage package)
    {
        _package = package;
        _orientation = new(package.Lookups.SinQ14);
        _inclinationCosine = package.Lookups.CosineConversion.ToArray();
        if (_inclinationCosine.Length != 128 || _inclinationCosine.Any(value => value is < 0 or > 256))
            throw new ArgumentException("The external inclination cosine table must have 128 values in 0..256.", nameof(package));
        ResetRun();
    }

    public SourcePlayerRawState State => new(
        _tick, _x, _y, _z, _yaw, _pitch, _roll,
        _velocity.X, _velocity.Y, _velocity.Z,
        _bodyVelocity.X, _bodyVelocity.Y, _bodyVelocity.Z,
        _worldAcceleration.X, _worldAcceleration.Y, _worldAcceleration.Z,
        _angularVelocity.X, _angularVelocity.Y, _angularVelocity.Z,
        _angularAcceleration.X, _angularAcceleration.Y, _angularAcceleration.Z,
        _control.EnginePower, _control.Steering, _force,
        _control.BoostReserve, _control.BoostSubunit, _damage,
        _offRoadTicks, 0, _recoveries, _groundedDelay, _damagedCount, _damageHoles, _smashCountdown, _sideFlags,
        _control.AcceleratingLatch, _control.Boosting, _touching, false, !_touching, _offMap,
        _smallerLimitRequired,
        _section, _segment, _contacts[0].Snapshot, _contacts[1].Snapshot, _contacts[2].Snapshot);

    public void ResetRun()
    {
        _tick = 0;
        _recoveries = 0;
        _control = new(false, 240, 0, _package.StandardBoostReserve, 0, false);
        Place(_package.Start.SectionIndex, initial: true);
    }

    public void Recover()
    {
        _recoveries++;
        Place(_section, initial: false);
    }

    public void Step(DriverInput input)
    {
        SourcePlayerControl.Validate(input);
        if (_offRoadTicks > 64)
            Recover();

        _control = SourcePlayerControl.Step(_control, input, _bodyVelocity.Z, _touching);
        var basis = _orientation.At(_yaw, _pitch, _roll);
        SampleContacts(basis);
        var body = basis.ToBody(_velocity);
        _bodyVelocity = new(body.X, 0, body.Z);
        var gravity = basis.ToBody(new(0, -SourceDynamicsMath.Gravity, 0));
        var collision = Collision(out var pitchResponse, out var rollResponse);
        var acceleration = BodyAcceleration(gravity, collision);
        var yawAcceleration = Steering();
        _worldAcceleration = basis.ToWorld(acceleration);
        ApplyDrag(collision.Z);
        _angularAcceleration = new(
            pitchResponse - (_angularVelocity.X >> 4) + (_touching ? acceleration.Z >> 2 : 0),
            yawAcceleration, rollResponse - (_angularVelocity.Z >> 4));
        _angularVelocity = Advance(_angularVelocity, _angularAcceleration);

        var sinRoll = _orientation.Sin(_roll);
        var cosRoll = _orientation.Cos(_roll);
        var finalPitch = SourceDynamicsMath.MultiplyQ14(_angularVelocity.X, cosRoll) +
                         SourceDynamicsMath.MultiplyQ14(_angularVelocity.Y, -sinRoll);
        var finalYaw = SourceDynamicsMath.MultiplyQ14(_angularVelocity.X, sinRoll) +
                       SourceDynamicsMath.MultiplyQ14(_angularVelocity.Y, cosRoll);
        var finalRoll = _angularVelocity.Z +
                        SourceDynamicsMath.MultiplyQ14(finalYaw, _orientation.Sin(_pitch));
        _velocity = Advance(_velocity, _worldAcceleration);
        _x = SourceDynamicsMath.IntegrateHorizontal(_x, _velocity.X);
        _y = Math.Min(268_435_456, SourceDynamicsMath.IntegrateVertical(_y, _velocity.Y));
        _z = SourceDynamicsMath.IntegrateHorizontal(_z, _velocity.Z);
        _pitch = SourceDynamicsMath.WrapAngle(_pitch + SourceDynamicsMath.AdvanceVelocity(0, finalPitch));
        _yaw = SourceDynamicsMath.WrapAngle(_yaw + SourceDynamicsMath.AdvanceVelocity(0, finalYaw));
        _roll = SourceDynamicsMath.WrapAngle(_roll + SourceDynamicsMath.AdvanceVelocity(0, finalRoll));
        LimitInclination();

        _offMap = _rearOffRoadDistance >= 64;
        if (!_offMap)
        {
            _offRoadTicks = 0;
            _smallerLimitRequired = false;
        }
        else if (_touching && _y < 16_777_216)
        {
            _offRoadTicks++;
            _smallerLimitRequired = true;
        }
        UpdateDamage();
        _tick++;
    }

    private void SampleContacts(SourceBasis basis)
    {
        var sinPitch = _orientation.Sin(_pitch);
        var sinRoll = _orientation.Sin(_roll);
        _sideFlags = 0;
        for (var index = 0; index < 3; index++)
        {
            var side = index == 0 ? -64 : index == 1 ? 64 : 0;
            var forward = index == 2 ? -128 : 128;
            var x = (long)_x + (long)side * basis.Right.X + (long)forward * basis.Forward.X;
            var z = (long)_z + (long)side * basis.Right.Z + (long)forward * basis.Forward.Z;
            var onRoad = _package.TrySampleRawRoad(x, z, (long)_y * 16, out var road);
            var distance = 0;
            var height = 4096;
            var floor = false;
            if (onRoad)
                height = SourceSnapshotAdapter.ToWheelRoadHeight(road.HeightQ14);
            else
            {
                var nearest = NearestEdge(x, z);
                road = nearest.Contact;
                distance = nearest.Distance;
                if (distance <= 48)
                {
                    height = SourceSnapshotAdapter.ToWheelRoadHeight(road.HeightQ14) - 16 * distance - 256;
                    floor = height < 4096;
                    height = Math.Max(4096, height);
                }
                else
                    floor = true;
            }
            _sideFlags = (_sideFlags >> 1) | (floor ? 128 : 0);
            var contact = _contacts[index];
            if (Math.Abs((long)_bodyVelocity.Z) < 2560 && Math.Abs((int)unchecked((short)_pitch)) < 1536)
                height = (height + contact.RoadHeight) / 2;
            contact.RoadHeight = height;
            contact.OnRoad = onRoad;
            contact.ActualHeight = index switch
            {
                0 => (_y + 32 * sinPitch + 16 * sinRoll) >> 8,
                1 => (_y + 32 * sinPitch - 16 * sinRoll) >> 8,
                _ => (_y - 32 * sinPitch) >> 8
            };
            if (index == 2)
            {
                _rearOffRoadDistance = distance;
                _section = road.SectionIndex;
                _segment = road.SegmentIndex;
            }
        }
    }

    private SourceVector Collision(out int pitchResponse, out int rollResponse)
    {
        if (_groundedDelay > 0)
            _groundedDelay--;
        _damageValue = 0;
        _damagedThisTick = false;
        var groundedEvent = false;
        foreach (var contact in _contacts)
        {
            contact.RawDifference = contact.RoadHeight - contact.ActualHeight;
            var response = SourceDynamicsMath.ContactResponse(
                contact.RoadHeight, contact.ActualHeight, contact.Penetration);
            contact.PreviousPenetration = contact.Penetration;
            contact.Penetration = response.Penetration;
            var force = response.Response;
            groundedEvent |= force >= 1024 && contact.Response < 512;
            if (force >= 1792)
            {
                _damageValue = Math.Max(_damageValue, force);
                _damagedCount++;
                if (_damagedCount < 10)
                {
                    var damage = ((force - 1536) / 256) & 255;
                    contact.Damage = (byte)Math.Min(255, contact.Damage + damage + damage / 2);
                    _damagedThisTick = true;
                }
                if (force >= 4608)
                    force = 4607;
            }
            else
                _damagedCount = 0;
            contact.Response = force;
        }
        if (groundedEvent && _groundedDelay == 0)
            _groundedDelay = 5;
        var left = _contacts[0];
        var right = _contacts[1];
        var rear = _contacts[2];
        _force = SourceDynamicsMath.AverageResponse(left.Response, right.Response, rear.Response);
        _touching = _force != 0;
        pitchResponse = SourceDynamicsMath.PitchResponse(left.Response, right.Response, rear.Response);
        rollResponse = SourceDynamicsMath.RollResponse(left.Response, right.Response);
        if (!_touching && unchecked((short)_pitch) >= 0 && _angularVelocity.X >= -256)
        {
            var target = _pitch >= 4096 ? -256 : -128;
            var difference = target - pitchResponse;
            if (difference < 0)
                pitchResponse = difference;
        }

        var pitchInclination = (((left.RawDifference + right.RawDifference) >> 1) - rear.RawDifference) >> 4;
        var rollInclination = (left.RawDifference - right.RawDifference) >> 3;
        var pitchSin = (int)Math.Min(Math.Abs((long)pitchInclination), 255);
        var rollSin = (int)Math.Min(Math.Abs((long)rollInclination), 255);
        var pitchCos = _inclinationCosine[pitchSin / 2];
        var rollCos = _inclinationCosine[rollSin / 2];
        var vertical = (pitchCos * rollCos) >> 8;
        var lateral = (pitchCos * rollSin) >> 8;
        return new(
            SourceDynamicsMath.MultiplyQ8(_force, rollInclination < 0 ? -lateral : lateral),
            SourceDynamicsMath.MultiplyQ8(_force, vertical),
            SourceDynamicsMath.MultiplyQ8(_force, pitchInclination < 0 ? pitchSin : -pitchSin));
    }

    private SourceVector BodyAcceleration(SourceVector gravity, SourceVector collision)
    {
        var grip = SourceDynamicsMath.TwiceCollisionYAcceleration(_touching, collision.Y);
        var engine = _control.EnginePower;
        var reduction = ((engine / 256) | (_bodyVelocity.Z / 256)) & 255;
        if ((reduction & 128) == 0 && (engine & 255) != 0)
            engine -= reduction;
        if (Math.Abs((long)engine) >= grip)
            engine = engine < 0 ? -grip : grip;
        var lateral = gravity.X + collision.X;
        var difference = lateral - _bodyVelocity.X;
        var ax = Math.Abs((long)difference) >= grip ?
            lateral - (_bodyVelocity.X < 0 ? -grip : grip) : collision.X - _bodyVelocity.X;
        return new(ax, gravity.Y + collision.Y, engine + gravity.Z + collision.Z);
    }

    private void ApplyDrag(int roadCollisionZ)
    {
        var amount = Math.Max(Math.Abs((long)_bodyVelocity.X), Math.Abs((long)_bodyVelocity.Z));
        var shift = 5;
        if (_touching && (Math.Abs(roadCollisionZ >> 8) >= 3 || _offMap))
        {
            amount = 24_576;
            shift = 1;
        }
        int Drag(int value) => (int)(((long)value * amount >> 16) >> shift);
        _worldAcceleration = new(
            _worldAcceleration.X - Drag(_velocity.X),
            _worldAcceleration.Y - Drag(_velocity.Y),
            _worldAcceleration.Z - Drag(_velocity.Z));
    }

    private int Steering()
    {
        var road = CurrentRoad();
        var section = _package.Layout.Sections[_section];
        var metadata = _package.TemplateMetadata.Single(item => item.Id == section.TemplateId);
        var difference = (int)unchecked((short)(road.HeadingAngle - _yaw));
        var backwards = Math.Abs(difference) > 16_384;
        if (difference > 16_384) difference -= 32_768;
        if (difference < -16_384) difference += 32_768;
        var curve = metadata.Type is 128 or 192;
        var rightBend = (metadata.Type == 128) ^ section.Reversed ^ backwards;
        if (curve)
            difference += rightBend ? 217 : -217;
        var magnitude = Math.Abs(difference);
        var scaled = magnitude < 2048 ? magnitude * 16 : 32_767;
        var right = _control.Steering > 0;
        var amount = metadata.SteeringAmount;
        var assistance = true;
        if (_control.Steering != 0)
        {
            if (curve)
            {
                if (right == rightBend)
                    amount += 45;
                else
                {
                    amount -= 35;
                    right = rightBend;
                    assistance = false;
                }
            }
            if (assistance && right == (difference >= 0))
                amount += scaled >> 8;
        }
        else
        {
            amount = curve ? amount : 0;
            right = rightBend;
        }
        if ((!curve && _control.Steering == 0) || (assistance && magnitude >= 7680))
        {
            var correction = magnitude >= 7680 ? magnitude - 7680 :
                Math.Max(1, (int)((long)Math.Min(magnitude, 255) *
                    Math.Min(Math.Abs((long)_bodyVelocity.Z) + 2560, 32_512) >> 15));
            _yaw = SourceDynamicsMath.WrapAngle(_yaw + (difference < 0 ? -correction : correction));
        }
        var yawTarget = SourceDynamicsMath.MultiplyQ8(_bodyVelocity.Z, amount);
        if (!right)
            yawTarget = -yawTarget;
        yawTarget >>= 3;
        return _touching ? yawTarget - _angularVelocity.Y : 0;
    }

    private SourceRoadContact CurrentRoad()
    {
        if (_package.TrySampleRawRoad(_x, _z, (long)_y * 16, out var road))
        {
            _section = road.SectionIndex;
            _segment = road.SegmentIndex;
            return road;
        }
        return NearestEdge(_x, _z).Contact;
    }

    private (SourceRoadContact Contact, int Distance) NearestEdge(long x, long z)
    {
        var best = Int128.MaxValue;
        var result = default(SourceRoadContact);
        foreach (var segment in _package.RawRoad)
        {
            Edge(segment.LeftStart.X, segment.LeftStart.Y, segment.LeftStart.Z,
                segment.LeftEnd.X, segment.LeftEnd.Y, segment.LeftEnd.Z, segment);
            Edge(segment.RightStart.X, segment.RightStart.Y, segment.RightStart.Z,
                segment.RightEnd.X, segment.RightEnd.Y, segment.RightEnd.Z, segment);
        }
        var low = 0;
        var high = 65;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var raw = (long)middle * 16_384;
            if ((Int128)raw * raw <= best) low = middle;
            else high = middle - 1;
        }
        return (result, low);

        void Edge(long ax, long ay, long az, long bx, long by, long bz, SourceRoadSegment segment)
        {
            var dx = (Int128)(bx - ax) * 16_384;
            var dz = (Int128)(bz - az) * 16_384;
            var px = (Int128)x - ax * 16_384;
            var pz = (Int128)z - az * 16_384;
            var denominator = dx * dx + dz * dz;
            if (denominator == 0)
                return;
            var numerator = Int128.Clamp(px * dx + pz * dz, 0, denominator);
            var nearX = ax * 16_384 + numerator * dx / denominator;
            var nearZ = az * 16_384 + numerator * dz / denominator;
            var distance = ((Int128)x - nearX) * ((Int128)x - nearX) +
                           ((Int128)z - nearZ) * ((Int128)z - nearZ);
            if (distance >= best)
                return;
            var height = checked((long)(((Int128)ay * denominator + (by - ay) * numerator) * 16_384 / denominator));
            result = new(height, 0, 0, 0, 0, segment.HeadingAngle, segment.SectionIndex, segment.Index);
            best = distance;
        }
    }

    private void LimitInclination()
    {
        var bound = _sideFlags == 224 && _smallerLimitRequired ? 2816 : 11_520;
        var pitch = Limit(_pitch, _angularVelocity.X, bound, out var pitchVelocity);
        var roll = Limit(_roll, _angularVelocity.Z, bound, out var rollVelocity);
        _pitch = pitch;
        _roll = roll;
        _angularVelocity = new(pitchVelocity, _angularVelocity.Y, rollVelocity);

        static ushort Limit(ushort raw, int velocity, int bound, out int finalVelocity)
        {
            var angle = (int)unchecked((short)raw);
            var limited = Math.Clamp(angle, -bound, bound);
            finalVelocity = angle != limited &&
                ((velocity >= 0 && limited < 0) || (velocity < 0 && limited >= 0)) ? 0 : velocity;
            return unchecked((ushort)limited);
        }
    }

    private void Place(int requestedSection, bool initial)
    {
        _section = requestedSection;
        _velocity = default;
        _bodyVelocity = default;
        _worldAcceleration = default;
        _angularVelocity = default;
        _angularAcceleration = default;
        _pitch = _roll = 0;
        _force = _damage = _damagedCount = _damageHoles = _smashCountdown = _groundedDelay = 0;
        _offRoadTicks = _rearOffRoadDistance = 0;
        _sideFlags = _damageValue = 0;
        _smallerLimitRequired = _damagedThisTick = false;
        _touching = _offMap = false;
        _control = _control with { AcceleratingLatch = false, EnginePower = 240, Steering = 0, Boosting = false };
        foreach (var contact in _contacts)
            contact.Reset();

        if (!initial)
        {
            for (var attempt = 0; attempt < _package.Layout.SectionCount; attempt++)
            {
                var section = _package.Layout.Sections[_section];
                var metadata = _package.TemplateMetadata.Single(item => item.Id == section.TemplateId);
                if ((metadata.RestartFlags & 128) == 0)
                {
                    const long cube = 67_108_864;
                    var x = section.GridX * cube + cube / 2;
                    var z = section.GridZ * cube + cube / 2;
                    if (_package.TrySampleRawRoad(x, z, 0, out var road))
                    {
                        _yaw = unchecked((ushort)(section.QuarterTurns * 16_384 +
                            (section.Reversed ? 32_768 : 0) + (metadata.Type == 64 ? 8192 : 0)));
                        _x = checked((int)(x + 160L * _orientation.Cos(_yaw)));
                        _z = checked((int)(z - 160L * _orientation.Sin(_yaw)));
                        _y = checked((SourceSnapshotAdapter.ToWheelRoadHeight(road.HeightQ14) + 3072) * 256);
                        _segment = road.SegmentIndex;
                        return;
                    }
                }
                _section = (_section + _package.Layout.SectionCount - 1) % _package.Layout.SectionCount;
            }
        }
        _section = _package.Start.SectionIndex;
        _segment = _package.Start.SegmentIndex;
        _x = checked((int)_package.Start.XQ14);
        _y = checked((int)_package.Start.InitialRawY);
        _z = checked((int)_package.Start.ZQ14);
        _yaw = unchecked((ushort)_package.Start.HeadingAngle);
    }

    private static SourceVector Advance(SourceVector velocity, SourceVector acceleration) => new(
        SourceDynamicsMath.AdvanceVelocity(velocity.X, acceleration.X),
        SourceDynamicsMath.AdvanceVelocity(velocity.Y, acceleration.Y),
        SourceDynamicsMath.AdvanceVelocity(velocity.Z, acceleration.Z));

    private void UpdateDamage()
    {
        if (_damagedThisTick)
            _damage = (((int)_contacts[0].Damage + _contacts[1].Damage) / 2 + _contacts[2].Damage) / 2;
        if (_smashCountdown > 0)
            _smashCountdown--;
        else if (_damagedThisTick && _damageValue >= 5120)
        {
            _damageHoles++;
            _smashCountdown = 69;
        }
    }

    private sealed class Contact
    {
        public int RoadHeight;
        public int ActualHeight;
        public int RawDifference;
        public int Penetration;
        public int PreviousPenetration;
        public int Response;
        public byte Damage;
        public bool OnRoad;
        public SourcePlayerContact Snapshot => new(RoadHeight, ActualHeight, Penetration,
            PreviousPenetration, Response, OnRoad, Damage);

        public void Reset()
        {
            RoadHeight = 4096;
            ActualHeight = RawDifference = Penetration = PreviousPenetration = Response = 0;
            Damage = 0;
            OnRoad = false;
        }
    }
}
