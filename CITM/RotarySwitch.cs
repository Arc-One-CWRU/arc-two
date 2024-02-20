using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Microsoft.DirectX;

using Demo3D.Common;
using Demo3D.Utilities;
using Demo3D.Visuals;
using Demo3D.PLC;

namespace Demo3D.Components {
    
    using Properties;
    
    public enum RotarySwitchControlMode {
        None = 0,
        Position = 1,
    }

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.ControlPanels_Category))]
    [HelpUrl("rotary_switch")]
    public class RotarySwitch : ExportableVisualAspect {
        private RotarySwitchControlMode controlMode = RotarySwitchControlMode.None;
        private VisualNormal 			rotationAxis;
        private VisualPoint 			rotationAnchor;

        [Required]
        public VisualNormal RotationAxis  { get => rotationAxis; set => SetProperty(ref rotationAxis, value); }

        [Required]
        public VisualPoint RotationAnchor { get => rotationAnchor; set => SetProperty(ref rotationAnchor, value); }

        [DefaultValue(0)/*, GreaterThanOrEqualTo(0)*/]
        public int DefaultPosition { get; set; } = 0;

        [DefaultValue(2)/*, GreaterThanOrEqualTo(2)*/]
        public int PositionCount { get; set; } = 2;

        [Angle, DefaultValue(30.0)/*, GreaterThan(0)*/]
        public double RotationRange { get; set; } = 30;

        [DefaultValue(RotarySwitchControlMode.None)]
        public RotarySwitchControlMode ControlMode {
            get { return controlMode; }
            set {
                if (SetProperty(ref controlMode, value)) {
                    UpdateBindingInterface();
                }
            }
        }

        [Auto] WriteToServer<int> Position;

        protected override IEnumerable<BindableItem> BindingInterface {
            get {
                if (controlMode == RotarySwitchControlMode.Position) {
                    yield return Position;
                }
            }
        }

        void AddVisualListeners() {
            RemoveVisualListeners();
            Visual.OnClick.NativeListeners += OnClick_NativeListeners;
        }

        void RemoveVisualListeners() {
            Visual.OnClick.NativeListeners -= OnClick_NativeListeners;
        }

        protected override void OnAdded() {
            base.OnAdded();

            // Ensure that we reset back to this position/orientation on reset.
            Visual.InitialPositionOnReset = true;
            Visual.SetInitialPosition();

            // Allow OnClick but not Drag
            Visual.Draggable = false;
            Visual.SelectParentWhenPicked = false;
            Visual.IsStatic = false;
        }

        protected override void OnEnabled() {
            Position.ValueChangedListeners += Position_ValueChangedListeners;

            AddVisualListeners();
        }

        protected override void OnDisabled() {
            RemoveVisualListeners();

            Position.ValueChangedListeners -= Position_ValueChangedListeners;
        }

        protected override void OnInitialize() {
            Position.Value = DefaultPosition;

            AddVisualListeners();
        }

        protected override void OnReset() {
            Position.Value = DefaultPosition;

            AddVisualListeners();
        }

        void OnClick_NativeListeners(Visual sender, PickInfo arg) {
            Position.Value = (Position.Value + 1) % PositionCount;
        }

        void Position_ValueChangedListeners(BindableItem obj) {
            var step = RotationRange / (PositionCount - 1);
            var angle = (Position - DefaultPosition) * step;

            Visual.WorldMatrix = Visual.InitialWorldMatrix 
                * Matrix.RotationAxisDegrees(RotationAxis.WorldNormal, angle, RotationAnchor.WorldLocation);
        }
    }
}
