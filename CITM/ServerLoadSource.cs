using System.Collections;
using System.Collections.Generic;
using System.Xml.Serialization;

using Demo3D.Native;
using Demo3D.Common;
using Demo3D.Visuals;
using Demo3D.Gui.AspectViewer;
using Demo3D.Gui.AspectViewer.Editors;
using Demo3D.PLC;
using Demo3D.PLC.Comms;

namespace Demo3D.Components {

    [Resources(typeof(Properties.Resources))]
    [HelpUrl("serverloadsource")]
    public class ServerLoadSource : LoadSource, IBindableItemOwner {
        private BindableItem createLoadBindableItem;

        [AspectProperty(IsVisible = false)]
        [XmlIgnore]
        public BindableItem CreateLoadBindableItem
        {
            get { return createLoadBindableItem; }
            private set {
                if (createLoadBindableItem != value) {
                    if (createLoadBindableItem != null) {
                        createLoadBindableItem.ValueChanged -= OnCreateLoadBindableItemChanged;
                        createLoadBindableItem.DetachFromVisual();
                        createLoadBindableItem = null;
                    }

                    createLoadBindableItem = value;
                    if (createLoadBindableItem != null)
                    {
                        createLoadBindableItem.ValueChanged += OnCreateLoadBindableItemChanged;
                        createLoadBindableItem.DefaultAccess = AccessRights.ReadFromPLC;
                        createLoadBindableItem.Value = false;
                    }
                }
            }
        }

        [AspectProperty]
        [XmlIgnore]
        public bool CreateLoad
        {
            get { return createLoadBindableItem?.ValueAs<bool>() ?? false; }
            set
            {
                if (createLoadBindableItem != null && createLoadBindableItem.ValueAs<bool>() != value)
                {
                    createLoadBindableItem.Value = value;
                    RaisePropertyChanged(nameof(CreateLoad));
                }
            }
        }

        IEnumerable<BindableItem> IBindableItemOwner.BindableItems {
            get {
                if (CreateLoadBindableItem != null) yield return CreateLoadBindableItem;
            }
        }

        protected override void OnAssigned() {
            base.OnAssigned();

            CleanupBindingAPI();

            if (Visual != null) {
                CreateBindings();
            } else {
                RemoveBindings();
            }
        }

        private void OnCreateLoadBindableItemChanged(BindableItem item) {
            if (item.ValueAs<bool>() == true) {
                var sensor = Visual.FindAspect<CollisionSensorAspect>();
                if (CongestionZone == false || sensor == null || sensor.IsBlocked == false) {
                    CloneVisual();
                }
            }
        }

        private void CreateBindings() {
            if (Visual == null) { return; }
            CreateLoadBindableItem = new ReadFromServer<bool>(Visual, BindingName("CreateLoad"));
            UpdateBindings();
        }

        private void RemoveBindings() {
            CreateLoadBindableItem = null;
            ReleaseBindingName(nameof(CreateLoad));
            UpdateBindingAPI();
        }

        private void UpdateBindings() {
            foreach (var bindableItem in ((IBindableItemOwner)this).BindableItems)
            {
                bindableItem.IsBindingInterface = TriStateYNM.No;
            }

            if (Visual == null) { return; }

            if (CreateLoadBindableItem != null) { CreateLoadBindableItem.IsBindingInterface = TriStateYNM.Yes; }

            UpdateBindingAPI();
        }
    }
}
