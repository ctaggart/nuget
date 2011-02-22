using System;
using System.Management.Automation;
using NuGet.VisualStudio;

namespace NuGet.PowerShell.Commands {

    /// <summary>
    /// This project updates the specfied package to the specfied project.
    /// </summary>
    [Cmdlet(VerbsData.Update, "Package")]
    public class UpdatePackageCommand : ProcessPackageBaseCommand {

        public UpdatePackageCommand()
            : this(ServiceLocator.GetInstance<ISolutionManager>(),
                   ServiceLocator.GetInstance<IVsPackageManagerFactory>(),
                   ServiceLocator.GetInstance<IVsProgressEvents>()) {
        }

        public UpdatePackageCommand(ISolutionManager solutionManager, IVsPackageManagerFactory packageManagerFactory, IVsProgressEvents progressEvents)
            : base(solutionManager, packageManagerFactory, progressEvents) {
        }

        [Parameter(Position = 2)]
        [ValidateNotNull]
        public Version Version { get; set; }

        [Parameter(Position = 3)]
        [ValidateNotNullOrEmpty]
        public string Source { get; set; }

        [Parameter]
        public SwitchParameter IgnoreDependencies { get; set; }

        protected override IVsPackageManager CreatePackageManager() {
            if (!String.IsNullOrEmpty(Source)) {
                return PackageManagerFactory.CreatePackageManager(Source);
            }
            return base.CreatePackageManager();
        }

        protected override void ProcessRecordCore() {
            if (!SolutionManager.IsSolutionOpen) {
                // terminating
                ErrorHandler.ThrowSolutionNotOpenTerminatingError();
            }

            try {
                SubscribeToProgressEvents();
                IProjectManager projectManager = ProjectManager;
                PackageManager.UpdatePackage(projectManager, Id, Version, !IgnoreDependencies, this);
            }
            finally {
                UnsubscribeFromProgressEvents();
            }
        }
    }
}