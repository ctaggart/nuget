using System.Diagnostics.CodeAnalysis;
using System.Management.Automation;

using EnvDTE;
using NuGet.VisualStudio;

namespace NuGet.PowerShell.Commands {
    /// <summary>
    /// This cmdlet returns the list of project names in the current solution, 
    /// which is used for tab expansion.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.PowerShell", "PS1101:AllCmdletsShouldAcceptPipelineInput", Justification="Will investiage this one.")]
    [Cmdlet(VerbsCommon.Get, "Project", DefaultParameterSetName = ParameterAttribute.AllParameterSets)]
    [OutputType(typeof(Project))]
    public class GetProjectCommand : NuGetBaseCommand {
        private readonly ISolutionManager _solutionManager;

        public GetProjectCommand()
            : this(ServiceLocator.GetInstance<ISolutionManager>(),
                    ServiceLocator.GetInstance<IVsProgressEvents>()) {
        }

        public GetProjectCommand(ISolutionManager solutionManager, IVsProgressEvents progressEvents)
            : base(solutionManager, null, progressEvents) {
            _solutionManager = solutionManager;
        }


        [Parameter(Mandatory = true, Position = 0, ParameterSetName = "ByName")]
        [ValidateNotNullOrEmpty]
        [SuppressMessage("Microsoft.Performance", "CA1819:PropertiesShouldNotReturnArrays", Justification = "PowerShell API requirement")]
        public string[] Name { get; set; }

        [Parameter(Mandatory = true, ParameterSetName = "All")]
        public SwitchParameter All { get; set; }

        protected override void ProcessRecordCore() {
            if (!SolutionManager.IsSolutionOpen) {
                ErrorHandler.ThrowSolutionNotOpenTerminatingError();
            }

            if (All.IsPresent) {
                WriteObject(_solutionManager.GetProjects(), enumerateCollection: true);
            }
            else {
                // No name specified; return default project (if not null)
                if (Name == null) {
                    if (_solutionManager.DefaultProject != null) {
                        WriteObject(_solutionManager.DefaultProject);
                    }
                }
                else {
                    // get all projects matching name(s) - handles wildcards
                    WriteObject(GetProjectsByName(Name), enumerateCollection: true);
                }
            }
        }
    }
}