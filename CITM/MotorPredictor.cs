using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;

using Demo3D.Visuals;
using Demo3D.EventQueue;
using Demo3D.PLC;
using Demo3D.PLC.Comms;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.Utilities;
using Demo3D.Common;

using Geometry = Demo3D.Common.Geometry;

namespace Demo3D.Components
{
    using Properties;

    public struct DataPoint
    {
        public double Position;
        public double Time;

        public DataPoint(double position, double time)
        {
            Position = position;
            Time = time;
        }
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Motors_Category))]
    [HelpUrl("motor_predictor")]
    public class MotorPredictor : ExportableVisualAspect, IMotor, IBindableItemOwner
    {
        public enum Units
        {
            Linear,
            Angular
        }

        public enum Mode
        {
            Discontinuous,
            Continuous
        }

        private Units units = Units.Linear;
        private Mode angleMode = Mode.Discontinuous;
        private double predictedPosition = 0.0;
        private double predictedVelocity = 0.0;
        private double predictedAcceleration = 0.0;

        private BindableItem<double> sourcePositionBindableItem;

        private int count = 0;
        private DataPoint[] points = new DataPoint[3];

        private double timestep = 0.01;
        private double timeout = 0.15;
        private CancelableEvent simulationEvent;

        public event MotorStatePropertyChanged StateListeners;
        public event MotorDirectionPropertyChanged DirectionListeners;
        public event MotorSpeedChanged SpeedListeners;
        public event MotorSpeedChanged AccelListeners;
        public event MotorSpeedChanged DecelListeners;
        public event NotifyDistanceListener OnNotifyDistanceListeners;

        [AspectProperty(IsVisible = true)]
        public override string Name {
            get => base.Name;
            set => base.Name = value;
        }

        [AspectProperty(IsVisible = false)]
        public IEnumerable<BindableItem> BindableItems
        {
            get
            {
                if (SourcePositionBindableItem != null) { yield return SourcePositionBindableItem; }
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        private BindableItem<double> SourcePositionBindableItem
        {
            get { return sourcePositionBindableItem; }
            set
            {
                if (sourcePositionBindableItem != value)
                {
                    if (sourcePositionBindableItem != null)
                    {
                        sourcePositionBindableItem.ValueChanged -= OnSourcePositionBindableItemChanged;
                        sourcePositionBindableItem.DetachFromVisual();
                        sourcePositionBindableItem = null;
                    }

                    sourcePositionBindableItem = value;
                    if (sourcePositionBindableItem != null)
                    {
                        sourcePositionBindableItem.ValueChanged += OnSourcePositionBindableItemChanged;
                        sourcePositionBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                    }
                }
            }
        }

        [AspectProperty]
        [DefaultValue(Units.Linear)]
        public Units UnitType
        {
            get { return this.units; }
            set
            {
                var oldUnits = this.units;
                if (SetProperty(ref this.units, value))
                {
                    RaisePropertyChanged(nameof(PositionUnits));
                    RaisePropertyChanged(nameof(VelocityUnits));
                    RaisePropertyChanged(nameof(AccelerationUnits));
                    RaisePropertyChanged(nameof(IsAngular));
                }
            }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute PositionUnits
        {
            get
            {
                switch (units)
                {
                    case Units.Linear: return new DistanceAttribute();
                    case Units.Angular: return new AngleAttribute();
                }

                return null;
            }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute VelocityUnits
        {
            get
            {
                switch (units)
                {
                    case Units.Linear: return new SpeedAttribute();
                    case Units.Angular: return new AngularSpeedAttribute();
                }

                return null;
            }
        }

        [AspectProperty(IsVisible = false)]
        public UnitsAttribute AccelerationUnits
        {
            get
            {
                switch (units)
                {
                    case Units.Linear: return new AccelerationAttribute();
                    case Units.Angular: return new AngularAccelerationAttribute();
                }

                return null;
            }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsAngular
        {
            get { return UnitType == Units.Angular; }
        }

        [AspectProperty]
        [AspectEditor(IsVisiblePropertyLink = nameof(IsAngular), IsEnabledPropertyLink = nameof(IsAngular))]
        [DefaultValue(Mode.Discontinuous)]
        public Mode AngleMode
        {
            get { return angleMode; }
            set { SetProperty(ref angleMode, value); }
        }

        [AspectProperty]
        [Time]
        [DefaultValue(0.01)]
        public double TimeStep
        {
            get { return timestep; }
            set
            {
                if (SetProperty(ref timestep, value))
                {
                    if (Visual != null && Visual.Document != null)
                    {
                        QueueOrCancelEvent();
                    }
                }
            }
        }

        [AspectProperty]
        [Time]
        [DefaultValue(0.15)]
        public double Timeout
        {
            get { return timeout; }
            set { SetProperty(ref timeout, value); }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits))]
        [XmlIgnore]
        public double SourcePosition
        {
            get { return sourcePositionBindableItem?.ValueAs<double>() ?? 0.0; }
            set
            {
                if (sourcePositionBindableItem != null)
                {
                    sourcePositionBindableItem.Value = value;
                }
            }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(PositionUnits))]
        public double PredictedPosition
        {
            get { return predictedPosition; }
            private set { SetProperty(ref predictedPosition, value); }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(VelocityUnits))]
        public double PredictedVelocity
        {
            get { return predictedVelocity; }
            private set { SetProperty(ref predictedVelocity, value); }
        }

        [AspectProperty]
        [LinkedUnitsAttributeEditorAttribute(UnitsAttributePropertyLink = nameof(AccelerationUnits))]
        public double PredictedAcceleration
        {
            get { return predictedAcceleration; }
            private set { SetProperty(ref predictedAcceleration, value); }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public MotorState State
        {
            get { return (PredictedVelocity == 0.0 && PredictedAcceleration == 0.0) ? MotorState.Off : MotorState.On; }
            set
            {
                if (value == MotorState.Off)
                {
                    // If we switch the motor off then get rid of all but a single data point at the
                    // current position.
                    if (count > 0)
                    {
                        points[0].Position = SourcePosition;
                        count = 1;
                    }
                }

                // Otherwise, if the motor is switched on then we can't really do anything until
                // positions get reported to us.
            }
        }

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public MotorDirection Direction
        {
            get { return (PredictedVelocity >= 0.0) ? MotorDirection.Forwards : MotorDirection.Reverse; }
            set
            {
                // We can't really do anything sensible here...
            }
        }

        [AspectProperty(IsVisible = false)]
        public double DistanceTravelled
        {
            get { return PredictedPosition; }
        }

        [AspectProperty(IsVisible = false)]
        public bool IsSteady
        {
            get { return PredictedAcceleration == 0.0; }
        }

        [AspectProperty(IsVisible = false)]
        public double CurrentSpeed
        {
            get { return Math.Abs(PredictedVelocity); }
        }

        private void CreateBindings()
        {
            SourcePositionBindableItem = new ReadFromServer<double>(Visual, BindingName(nameof(SourcePosition)));
            SourcePositionBindableItem.IsBindingInterface = TriStateYNM.Yes;
        }

        private void RemoveBindings()
        {
            SourcePositionBindableItem = null;

            ReleaseBindingName(nameof(SourcePosition));

            UpdateBindingAPI();
        }

        protected override void OnAssigned()
        {
            base.OnAssigned();
        }

        protected override void OnAdded()
        {
            base.OnAdded();

            CleanupBindingAPI();
            CreateBindings();
        }

        protected override void OnRemoved()
        {
            base.OnRemoved();

            CleanupBindingAPI();
            RemoveBindings();
        }

        protected override void OnReset()
        {
            base.OnReset();
            count = 0;
            PredictedPosition = 0.0;
            PredictedVelocity = 0.0;
            PredictedAcceleration = 0.0;

            simulationEvent.Cancel();
            RaisePropertyChanged(nameof(PredictedPosition));
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            if (SourcePositionBindableItem != null)
            {
                Add(new DataPoint(SourcePositionBindableItem.ValueAs<double>(), document.Time));
            }

            QueueOrCancelEvent();
        }

        private void QueueOrCancelEvent()
        {
            var currentTime = document.EventQueue.Time;
            var nextTime = currentTime + new Fixed(timestep);
            if (simulationEvent.HasValue)
            {
                var eventTime = simulationEvent.Value.Time;
                if (eventTime.CompareTo(currentTime) > 0 && eventTime.CompareTo(nextTime) < 0)
                {
                    return;
                }
            }

            simulationEvent.Cancel();
            simulationEvent.Value = document.EventQueue.ScheduleActionAt(nextTime, Step, nameof(MotorPredictor));
        }

        private void Step()
        {
            // Move.
            Move(document.Time);

            // Schedule next step.
            QueueOrCancelEvent();
        }

        private void Move(double time)
        {
            if (IsEnabled)
            {
                Predict(time, out var position, out var velocity, out var acceleration);
                PredictedPosition = position;
                PredictedVelocity = velocity;
                PredictedAcceleration = acceleration;
            }
            else
            {
                PredictedPosition = SourcePosition;
                PredictedVelocity = 0.0;
                PredictedAcceleration = 0.0;
            }
        }

        private void Predict(double time, out double position, out double velocity, out double acceleration)
        {
            position = velocity = acceleration = 0.0;
            double dt = (count > 0) ? time - points[count - 1].Time : 0.0;

            if (dt >= timeout)
            {
                position = (count > 0) ? points[count - 1].Position : 0.0;
                velocity = 0.0;
                acceleration = 0.0;
                return;
            }

            switch (count)
            {
                case 1:
                    position = Position(points[0].Position);
                    break;
                case 2:
                    velocity = CalculateVelocity(points[0], points[1]);
                    position = Position(points[1].Position + velocity * dt);
                    break;
                case 3:
                    var initialVelocity = CalculateVelocity(points[1], points[2]);
                    acceleration = CalculateAcceleration(points[0], points[1], points[2]);
                    velocity = initialVelocity + acceleration * dt;
                    position = Position(points[2].Position + initialVelocity * dt + 0.5 * acceleration * dt * dt);
                    break;
            }
        }

        private void Add(DataPoint point)
        {
            if (count == 0 || points[count - 1].Time < point.Time)
            {
                if (count < 3)
                {
                    points[count] = point;
                    count += 1;
                }
                else
                {
                    points[0] = points[1];
                    points[1] = points[2];
                    points[2] = point;
                }
            }
        }

        private double Position(double value)
        {
            if (units == Units.Angular)
            {
                switch (angleMode)
                {
                    case Mode.Continuous: return Geometry.Util.NormaliseWrapAroundRange(value, 0.0, 360.0);
                }
            }

            return value;
        }

        private double CalculateDistance(DataPoint p0, DataPoint p1)
        {
            if (units == Units.Angular)
            {
                switch (angleMode)
                {
                    case Mode.Continuous: return Util.Degrees(Geometry.Util.SignedAngularDifference(Util.Radians(p0.Position), Util.Radians(p1.Position)));
                }
            }

            return p1.Position - p0.Position;
        }

        private double CalculateVelocity(DataPoint p0, DataPoint p1)
        {
            return CalculateDistance(p0, p1) / (p1.Time - p0.Time);
        }

        private double CalculateAcceleration(DataPoint p0, DataPoint p1, DataPoint p2)
        {
            var v0 = CalculateVelocity(p0, p1);
            var v1 = CalculateVelocity(p1, p2);
            return 2.0 * (v1 - v0) / (p2.Time - p0.Time);
        }

        private void OnSourcePositionBindableItemChanged(BindableItem obj)
        {
            Add(new DataPoint(obj.ValueAs<double>(), document.Time));
        }

        public object AddDistanceNotifier(double distance, Visual visual)
        {
            return null;
        }

        public void RemoveDistanceNotifier(object handle)
        {
            // Nothing to do.
        }
    }
}