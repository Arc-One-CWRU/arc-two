using System;
using System.ComponentModel;

using Demo3D.Common;
using Demo3D.Visuals;

namespace Demo3D.Components
{
    using Properties;

    [Resources(typeof(Resources))]
    [Category(nameof(Resources.Loads_Category))]
    [HelpUrl("quick_load")]
    public class QuickLoad : ExportableVisualAspect, ILoadAspectManager
    {
        private bool deleteOnReset = false;
        private bool deleteWhenFloorHit = false;

        [DefaultValue(false)]
        public bool DeleteOnReset
        {
            get { return deleteOnReset; }
            set
            {
                SetProperty(ref deleteOnReset, value);
                if (Visual != null)
                {
                    var loadAspect = Visual.FindAspect<LoadAspect>();
                    if (loadAspect != null)
                    {
                        loadAspect.DeleteOnReset = deleteOnReset;
                    }
                }
            }
        }

        [DefaultValue(false)]
        public bool DeleteWhenFloorHit
        {
            get { return deleteWhenFloorHit; }
            set
            {
                SetProperty(ref deleteWhenFloorHit, value);
                if (Visual != null)
                {
                    var loadAspect = Visual.FindAspect<LoadAspect>();
                    if (loadAspect != null)
                    {
                        loadAspect.DeleteWhenFloorHit = deleteWhenFloorHit;
                    }
                }
            }
        }

        protected override bool CanAdd(ref string reasonForFailure)
        {
            if (Visual is CoreVisual)
            {
                reasonForFailure = "quick load aspects cannot be added to core visuals";
                return false;
            }

            return true;
        }

        protected override void OnAdded()
        {
            base.OnAdded();

            // Add a rigid body if one does not already exist.
            var rigidBodyAspect = Visual.FindCreateAspect<RigidBodyAspect>();
            rigidBodyAspect.AspectManagedBy = this;

            // Add a physics group provider if one does not already exist.
            var groupAspect = Visual.FindAspect<IPhysicsGroupProvider>();
            if (groupAspect == null)
            {
                groupAspect = Visual.FindCreateAspect<LoadAspect>();
                if (groupAspect is VisualAspect visualAspect)
                {
                    visualAspect.AspectManagedBy = this;
                }
            }

            if (groupAspect is LoadAspect loadAspect)
            {
                loadAspect.DeleteOnReset = DeleteOnReset;
                loadAspect.DeleteWhenFloorHit = DeleteWhenFloorHit;
            }

            // Should we add some default physics geometry?
            // We only do this if the rigid body will otherwise have no geometry. Note that we need
            // to traverse the hierarchy since the rigid body can include geometry from descendant
            // visuals in the visual hierarchy.
            bool rigidBodyHasGeometry = false;
            var physicsGeometryAspects = Visual.FindVisualAndDescendantsAspects<PhysicsGeometryAspect>();
            if (physicsGeometryAspects != null)
            {
                foreach (var physicsGeometryAspect in physicsGeometryAspects)
                {
                    if (physicsGeometryAspect.UseAncestorBody == false)
                    {
                        if (Visual == physicsGeometryAspect.Visual)
                        {
                            rigidBodyHasGeometry = true;
                            break;
                        }
                    }
                    else
                    {
                        var physicsGeometryVisual = physicsGeometryAspect.Visual;
                        if (physicsGeometryVisual != null)
                        {
                            var geometryRigidBody = physicsGeometryVisual.FindVisualAndAncestorsAspect<RigidBodyAspect>();
                            if (geometryRigidBody == rigidBodyAspect)
                            {
                                rigidBodyHasGeometry = true;
                                break;
                            }
                        }
                    }
                }
            }

            // Add default physics geometry.
            if (rigidBodyHasGeometry == false)
            {
                var boxPhysics = Visual.FindCreateAspect<BoxPhysicsAspect>();
                boxPhysics.AspectManagedBy = this;
            }
        }
    }
}
