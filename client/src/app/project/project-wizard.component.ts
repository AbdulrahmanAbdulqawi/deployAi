import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { forkJoin, map, Observable, of, switchMap, throwError } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import {
  CredentialSummary,
  DatabaseRequirementProfile,
  DeploymentPlan,
  DeploymentPlanKind,
  EnvSchemaVar,
  DeploymentPlanPart,
  DeploymentReadinessResult,
  FrontendBuildProfile,
  ProjectDetail,
  ServerBuildProfile,
  GitHubBranch,
  GitHubRepo,
  ProviderProject,
  ProviderName,
  CoolifyInfrastructure,
  CoolifyInfrastructureResource,
  CoolifyBuildPack
} from '../core/models/api.models';
import { RepoFolderPickerComponent } from '../shared/repo-folder-picker/repo-folder-picker.component';
import { DeployPlanComponent } from '../shared/deploy-plan/deploy-plan.component';
import { DeploymentSetupPanelComponent } from '../shared/deployment-setup-panel/deployment-setup-panel.component';
import { ReadinessScorecardComponent } from '../shared/readiness-scorecard/readiness-scorecard.component';
import { usesCoolifySetupScaffold } from '../core/utils/readiness-scorecard';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { AppLogoPickerComponent } from '../shared/app-logo-picker/app-logo-picker.component';
import { ProjectsStore } from '../core/stores/projects.store';

interface EnvRow {
  key: string;
  value: string;
}

type DeploymentMode = 'website' | 'server' | 'both' | 'coolify' | 'coolify-fullstack';

@Component({
  selector: 'app-project-wizard',
  standalone: true,
  imports: [FormsModule, RepoFolderPickerComponent, DeployPlanComponent, DeploymentSetupPanelComponent, ReadinessScorecardComponent, ButtonComponent, IconComponent, AppLogoPickerComponent],
  templateUrl: './project-wizard.component.html',
  styleUrl: './project-wizard.component.scss'
})
export class ProjectWizardComponent implements OnInit {
  readonly step = signal(1);
  readonly repos = signal<GitHubRepo[]>([]);
  readonly branches = signal<GitHubBranch[]>([]);
  readonly credentials = signal<CredentialSummary[]>([]);
  readonly vercelProjects = signal<ProviderProject[]>([]);
  readonly railwayProjects = signal<ProviderProject[]>([]);
  readonly coolifyProjects = signal<ProviderProject[]>([]);
  readonly selectedCoolifyProjectId = signal<string | null>(null);
  readonly selectedCoolifyWebsiteProjectId = signal<string | null>(null);
  readonly selectedCoolifyServerProjectId = signal<string | null>(null);
  readonly selectedRepo = signal<GitHubRepo | null>(null);
  readonly selectedBranch = signal<string | null>(null);
  readonly selectedVercelProjectId = signal<string | null>(null);
  readonly selectedRailwayProjectId = signal<string | null>(null);
  readonly loadingCoolifyProjects = signal(false);
  readonly coolifyHostingMode = signal<'existing' | 'create'>('existing');
  readonly coolifyWebsiteHostingMode = signal<'existing' | 'create'>('existing');
  readonly coolifyServerHostingMode = signal<'existing' | 'create'>('existing');
  readonly creatingCoolifyProject = signal(false);
  readonly loadingCoolifyInfrastructure = signal(false);
  readonly coolifyInfrastructure = signal<CoolifyInfrastructure | null>(null);
  readonly loadingCoolifyEnvironments = signal(false);
  readonly coolifyEnvironments = signal<CoolifyInfrastructureResource[]>([]);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly loadingCredentials = signal(true);
  readonly loadingVercelProjects = signal(false);
  readonly loadingRailwayProjects = signal(false);
  readonly connectingVercelOAuth = signal(false);
  readonly connectingRailwayOAuth = signal(false);
  readonly creatingVercelProject = signal(false);
  readonly creatingRailwayProject = signal(false);
  readonly vercelHostingMode = signal<'existing' | 'create'>('existing');
  readonly railwayHostingMode = signal<'existing' | 'create'>('existing');
  readonly showAdvancedEnv = signal(false);
  readonly websiteBuildProfile = signal<FrontendBuildProfile | null>(null);
  readonly detectingWebsiteProfile = signal(false);
  readonly serverBuildProfile = signal<ServerBuildProfile | null>(null);
  readonly detectingServerProfile = signal(false);
  readonly databaseRequirements = signal<DatabaseRequirementProfile | null>(null);
  readonly detectingDatabaseRequirements = signal(false);
  readonly deploymentPlan = signal<DeploymentPlan | null>(null);
  readonly deploymentReadiness = signal<DeploymentReadinessResult | null>(null);
  readonly loadingDeploymentReadiness = signal(false);
  readonly loadingDeploymentPlan = signal(false);
  readonly showManualOverride = signal(false);
  readonly activePlanParts = signal<DeploymentPlanPart[]>([]);

  readonly stepTotal = computed(() => {
    if (this.showManualOverride() || this.step() > 3) {
      return 5;
    }
    return 3;
  });

  readonly wizardSteps = computed(() => {
    if (this.stepTotal() === 5) {
      return [
        { num: 1, label: 'Repository' },
        { num: 2, label: 'Branch' },
        { num: 3, label: 'What to deploy' },
        { num: 4, label: 'Hosting' },
        { num: 5, label: 'Review' }
      ];
    }
    return [
      { num: 1, label: 'Repository' },
      { num: 2, label: 'Branch' },
      { num: 3, label: 'Deploy' }
    ];
  });

  stepFillPercent(): number {
    const total = this.stepTotal();
    if (total <= 1) {
      return 0;
    }
    return ((this.step() - 1) / (total - 1)) * 100;
  }

  stepState(stepNum: number): 'complete' | 'active' | 'pending' {
    const current = this.step();
    if (stepNum < current) {
      return 'complete';
    }
    if (stepNum === current) {
      return 'active';
    }
    return 'pending';
  }

  introTitle(): string {
    switch (this.step()) {
      case 1:
        return 'Add app';
      case 2:
        return 'Select branch';
      case 3:
        return this.showManualOverride() ? 'What to deploy' : 'Deployment Plan';
      case 4:
        return 'Hosting setup';
      case 5:
        return 'Review & save';
      default:
        return 'Add app';
    }
  }

  introSubtitle(): string {
    switch (this.step()) {
      case 1:
        return 'Choose the GitHub repository you want DeployAI to analyze and deploy.';
      case 2:
        return 'Pick the branch DeployAI will use for deployment.';
      case 3:
        return this.showManualOverride()
          ? 'Tell us what parts of your repository should be deployed.'
          : 'We\'ve analyzed your repository and prepared a recommended configuration.';
      case 4:
        return 'Connect or select the hosting targets for your deployment.';
      case 5:
        return 'Confirm your app details before saving.';
      default:
        return 'We\'ll figure out where your code should live.';
    }
  }

  search = '';
  projectName = '';
  readonly selectedLogoKey = signal<string | null>(null);
  newVercelProjectName = '';
  newRailwayProjectName = '';
  newCoolifyProjectName = '';
  newCoolifyWebsiteProjectName = '';
  newCoolifyServerProjectName = '';
  selectedCoolifyCredentialId = '';
  selectedCoolifyGithubAppId = '';
  selectedCoolifyProjectUuid = '';
  selectedCoolifyServerUuid = '';
  selectedCoolifyEnvironmentName = '';
  selectedVercelCredentialId = '';
  selectedRailwayCredentialId = '';
  deploymentMode: DeploymentMode | null = null;
  publishCoolify = false;
  publishWebsite = false;
  publishServer = false;
  websiteRootPath = '';
  serverRootPath = '';
  websiteFolderSelected = false;
  serverFolderSelected = false;
  envRows: EnvRow[] = [{ key: '', value: '' }];

  constructor(
    private readonly api: ApiService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    private readonly projectsStore: ProjectsStore
  ) {}

  ngOnInit(): void {
    this.loadRepos();
    this.loadCredentials();

    const stepParam = this.route.snapshot.queryParamMap.get('step');
    if (stepParam === '4' || this.route.snapshot.queryParamMap.get('vercel') === 'connected' ||
        this.route.snapshot.queryParamMap.get('railway') === 'connected') {
      this.step.set(4);
    }
  }

  repoOwner(): string | null {
    const repo = this.selectedRepo()?.fullName;
    return repo?.split('/')[0] ?? null;
  }

  repoName(): string | null {
    const repo = this.selectedRepo()?.fullName;
    return repo?.split('/')[1] ?? null;
  }

  loadCredentials(): void {
    this.loadingCredentials.set(true);
    this.api.getCredentials().subscribe({
      next: (response) => {
        this.credentials.set(response.credentials);
        this.loadingCredentials.set(false);
        const vercel = response.credentials.find(c => c.providerName === ProviderName.Vercel);
        if (vercel) {
          this.selectedVercelCredentialId = vercel.id;
          this.loadVercelProjects();
        }
        const railway = response.credentials.find(c => c.providerName === ProviderName.Railway);
        if (railway) {
          this.selectedRailwayCredentialId = railway.id;
          this.loadRailwayProjects();
        }
        const coolify = response.credentials.find(c => c.providerName === ProviderName.Coolify);
        if (coolify) {
          this.selectedCoolifyCredentialId = coolify.id;
          this.loadCoolifyProjects();
        }
      },
      error: (err) => {
        this.loadingCredentials.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not load your connections.');
      }
    });
  }

  connectWithVercel(): void {
    this.connectingVercelOAuth.set(true);
    this.api.getVercelLoginUrl('/projects/new?step=4').subscribe({
      next: (response) => {
        window.location.href = response.url;
      },
      error: (err) => {
        this.connectingVercelOAuth.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not start Vercel connection.');
      }
    });
  }

  connectWithRailway(): void {
    this.connectingRailwayOAuth.set(true);
    this.api.getRailwayLoginUrl('/projects/new?step=4').subscribe({
      next: (response) => {
        window.location.href = response.url;
      },
      error: (err) => {
        this.connectingRailwayOAuth.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not start Railway connection.');
      }
    });
  }

  goToConnections(): void {
    void this.router.navigate(['/settings/connections'], {
      queryParams: { returnUrl: '/projects/new', step: 3 }
    });
  }

  isCoolifyFullStack(): boolean {
    return this.deploymentMode === 'coolify-fullstack';
  }

  /** A private repo deployed to Coolify needs a GitHub App picked so Coolify can read the code. */
  requiresCoolifyGithubApp(): boolean {
    if (!this.selectedRepo()?.private) {
      return false;
    }
    const kind = this.deploymentPlan()?.planKind;
    return kind === DeploymentPlanKind.CoolifyFullStack
      || kind === DeploymentPlanKind.CoolifyCompose
      || kind === DeploymentPlanKind.CoolifySingle;
  }

  /**
   * Reads the plan rather than assuming a shape. The hint used to promise "two Coolify
   * applications" unconditionally, which contradicted the single-origin compose plan the
   * analysis step now produces for exactly this stack.
   */
  coolifyFullStackHint(): string {
    if (this.deploymentPlan()?.planKind === DeploymentPlanKind.CoolifyCompose) {
      return 'DeployAI will create one Docker Compose deployment on your server — the site is ' +
        'served from your domain and forwards /api to the API service internally, so there is no ' +
        'second URL and no CORS to configure.';
    }

    return 'DeployAI will create two Coolify applications (website and API), wire environment ' +
      'variables between them, and provision PostgreSQL when needed.';
  }

  missingConnections(): string[] {
    const missing: string[] = [];
    if (this.isCoolifyFullStack()) {
      if (this.coolifyCredentials().length === 0) {
        missing.push('Coolify');
      }
      return missing;
    }
    if (this.publishWebsite && this.vercelCredentials().length === 0) {
      missing.push('Vercel');
    }
    if (this.publishServer && this.railwayCredentials().length === 0) {
      missing.push('Railway');
    }
    if (this.publishCoolify && this.coolifyCredentials().length === 0) {
      missing.push('Coolify');
    }
    return missing;
  }

  vercelCredentials(): CredentialSummary[] {
    return this.credentials().filter(c => c.providerName === ProviderName.Vercel);
  }

  railwayCredentials(): CredentialSummary[] {
    return this.credentials().filter(c => c.providerName === ProviderName.Railway);
  }

  coolifyCredentials(): CredentialSummary[] {
    return this.credentials().filter(c => c.providerName === ProviderName.Coolify);
  }

  loadRepos(): void {
    this.api.listRepos(1, 30, this.search || undefined).subscribe({
      next: (response) => this.repos.set(response.repos),
      error: (err) => this.error.set(err?.error?.error?.message ?? 'Could not load your GitHub apps.')
    });
  }

  selectRepo(repo: GitHubRepo): void {
    this.selectedRepo.set(repo);
    const repoName = repo.fullName.split('/')[1] ?? repo.fullName;
    this.projectName = repoName;
    this.onProjectNameChange();
  }

  onProjectNameChange(): void {
    const base = this.sanitizeName(this.projectName);
    this.newVercelProjectName = base;
    this.newRailwayProjectName = `${base}-api`;
    this.newCoolifyProjectName = base;
    this.newCoolifyWebsiteProjectName = base;
    this.newCoolifyServerProjectName = `${base}-api`;
  }

  private sanitizeName(name: string): string {
    return name
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9._-]+/g, '-')
      .replace(/-+/g, '-')
      .replace(/^[-.]+|[-.]+$/g, '') || 'app';
  }

  nextFromRepo(): void {
    const repo = this.selectedRepo();
    if (!repo) return;
    const [owner, name] = repo.fullName.split('/');
    this.api.listBranches(owner, name).subscribe({
      next: (response) => {
        this.branches.set(response.branches);
        this.selectedBranch.set(repo.defaultBranch);
        this.step.set(2);
      },
      error: (err) => this.error.set(err?.error?.error?.message ?? 'Could not load versions for that app.')
    });
  }

  enterPartsStep(): void {
    this.step.set(3);
    this.showManualOverride.set(false);
    this.activePlanParts.set([]);
    this.deploymentPlan.set(null);
    this.loadDeploymentPlan();
  }

  loadDeploymentPlan(): void {
    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    this.loadingDeploymentPlan.set(true);
    this.error.set(null);
    this.api.getDeploymentPlan(owner, name, branch).subscribe({
      next: (plan) => {
        this.deploymentPlan.set(plan);
        this.loadingDeploymentPlan.set(false);
        if (plan.confidence === 'high') {
          this.activePlanParts.set(plan.parts);
        }

        // A Coolify plan needs a destination project before it can be accepted. Loading it here
        // rather than only on the manual path is what lets the plan card offer the choice —
        // otherwise accepting fails with "pick which one" and there is nowhere to pick.
        if (plan.parts.some(part => part.providerName === ProviderName.Coolify)) {
          this.ensureCoolifyDestinations();
        }

        this.loadDeploymentReadiness();
      },
      error: (err) => {
        this.loadingDeploymentPlan.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not inspect that app.');
        this.showManualOverride.set(true);
      }
    });
  }

  loadDeploymentReadiness(): void {
    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    const parts = this.activePlanParts().length > 0
      ? this.activePlanParts()
      : this.deploymentPlan()?.parts ?? [];
    if (!owner || !name || !branch || parts.length === 0) {
      this.deploymentReadiness.set(null);
      return;
    }

    this.loadingDeploymentReadiness.set(true);
    this.api.scanDeploymentReadiness(owner, name, branch, parts).subscribe({
      next: (result) => {
        this.deploymentReadiness.set(result);
        this.loadingDeploymentReadiness.set(false);
      },
      error: () => {
        this.deploymentReadiness.set(null);
        this.loadingDeploymentReadiness.set(false);
      }
    });
  }

  acceptDeploymentPlan(): void {
    const parts = this.activePlanParts();
    if (parts.length === 0) {
      return;
    }

    this.applyPlanParts(parts);

    // A compose app boots straight into its env, so the env is collected *before* the first
    // deploy — the ordering proven live: without it the API crash-loops and nginx answers 502.
    if (this.isSingleOriginComposePlan()) {
      this.loadEnvSchema();
      this.showEnvStep.set(true);
      return;
    }

    this.deployFromPlan();
  }

  selectClarifyingOption(optionId: string): void {
    const question = this.deploymentPlan()?.clarifyingQuestion;
    const option = question?.options.find(item => item.id === optionId);
    if (!option) {
      return;
    }

    this.activePlanParts.set(option.resolvesToParts);
    this.applyPlanParts(option.resolvesToParts);
    this.loadDeploymentReadiness();
  }

  showManualConfiguration(): void {
    this.showManualOverride.set(true);
    this.deploymentMode = null;
    this.publishWebsite = false;
    this.publishServer = false;
    this.publishCoolify = false;
    this.websiteRootPath = '';
    this.serverRootPath = '';
    this.websiteFolderSelected = false;
    this.serverFolderSelected = false;
    this.websiteBuildProfile.set(null);
    this.serverBuildProfile.set(null);
    this.databaseRequirements.set(null);
  }

  canAcceptPlan(): boolean {
    const readiness = this.deploymentReadiness();
    if (readiness?.usesSplitOrigin && !readiness.isReady) {
      return false;
    }

    return this.activePlanParts().length > 0;
  }

  onSetupComplete(): void {
    this.loadDeploymentReadiness();
  }

  showReadinessScorecard(readiness: DeploymentReadinessResult): boolean {
    return readiness.usesSplitOrigin;
  }

  showDeploymentSetupPanel(readiness: DeploymentReadinessResult): boolean {
    return usesCoolifySetupScaffold(readiness);
  }

  deployFromPlan(): void {
    if (this.missingConnections().length > 0) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    this.ensureProviderProjects().pipe(
      switchMap(() => this.createProjectFromPlanRequest()),
      // Env lands between create and the first deploy — the planner's proven ordering — so a
      // compose app boots configured instead of crash-looping on missing secrets.
      switchMap(project => this.pushEnvironmentIfCollected(project.id).pipe(map(() => project))),
      switchMap(project =>
        this.api.triggerDeployment(project.id).pipe(
          map(response => ({ project, response }))
        )
      )
    ).subscribe({
      next: ({ project, response }) => {
        this.saving.set(false);
        this.projectsStore.refresh();
        void this.router.navigate(['/projects', project.id, 'deploy', response.deploymentId]);
      },
      error: (err) => {
        this.saving.set(false);
        this.error.set(err?.error?.error?.message ?? err?.message ?? 'Could not deploy your app.');
      }
    });
  }

  private pushEnvironmentIfCollected(projectId: string): Observable<unknown> {
    const variables = this.envSchema()
      .map(schema => ({
        key: schema.name,
        value: (this.envValues[schema.name] ?? '').trim(),
        isSecret: schema.isSecret
      }))
      .filter(variable => variable.value.length > 0);

    if (variables.length === 0) {
      return of(undefined);
    }

    return this.api.setComposeEnvironment(projectId, variables);
  }

  private ensureProviderProjects(): Observable<void> {
    const tasks: Observable<unknown>[] = [];

    if (this.publishWebsite && !this.selectedVercelProjectId()) {
      tasks.push(this.createVercelProjectRequest());
    }
    if (this.publishServer && !this.selectedRailwayProjectId()) {
      tasks.push(this.createRailwayProjectRequest());
    }
    if (this.publishCoolify && !this.selectedCoolifyProjectId()) {
      tasks.push(this.createCoolifyProjectRequest('server', this.newCoolifyProjectName));
    }
    if (this.isCoolifyFullStack()) {
      if (this.isSingleOriginComposePlan()) {
        // One compose resource hosts both halves, so there is one application to create —
        // creating two would leave an orphan alongside it.
        if (!this.selectedCoolifyWebsiteProjectId()) {
          tasks.push(this.createCoolifyProjectRequest('website', this.newCoolifyWebsiteProjectName));
        }
      } else {
        if (!this.selectedCoolifyWebsiteProjectId()) {
          tasks.push(this.createCoolifyProjectRequest('website', this.newCoolifyWebsiteProjectName));
        }
        if (!this.selectedCoolifyServerProjectId()) {
          tasks.push(this.createCoolifyProjectRequest('server', this.newCoolifyServerProjectName));
        }
      }
    }

    if (tasks.length === 0) {
      return of(undefined);
    }

    return forkJoin(tasks).pipe(map(() => undefined));
  }

  private createProjectFromPlanRequest(): Observable<ProjectDetail> {
    const repo = this.selectedRepo();
    const branch = this.selectedBranch();
    if (!repo || !branch) {
      return throwError(() => new Error('Choose an app and version first.'));
    }

    const parts = this.activePlanParts()
      .filter(part => part.role !== 'database')
      .map(part => {
        const isWebsite = part.role === 'website';
        const isCoolify = part.providerName === ProviderName.Coolify;
        const credentialId = isCoolify
          ? this.selectedCoolifyCredentialId
          : (isWebsite ? this.selectedVercelCredentialId : this.selectedRailwayCredentialId);
        const providerProjectId = isCoolify
          ? (isWebsite ? this.selectedCoolifyWebsiteProjectId() : this.selectedCoolifyServerProjectId())
          : (isWebsite ? this.selectedVercelProjectId() : this.selectedRailwayProjectId());
        if (!credentialId || !providerProjectId) {
          return null;
        }

        return {
          role: part.role,
          providerName: part.providerName,
          credentialId,
          providerProjectId,
          rootDirectory: part.rootDirectory,
          serviceDirectory: part.serviceDirectory,
          buildCommand: part.buildCommand,
          installCommand: part.installCommand,
          startCommand: part.startCommand,
          outputDirectory: part.outputDirectory,
          framework: part.framework,
          dockerfilePath: part.dockerfilePath
        };
      })
      .filter((part): part is NonNullable<typeof part> => part !== null);

    if (parts.length === 0) {
      return throwError(() => new Error('Hosting setup is incomplete.'));
    }

    const dbProfile = this.databaseRequirements();
    return this.api.createProjectFromPlan({
      name: this.projectName,
      githubRepoFullName: repo.fullName,
      defaultBranch: branch,
      logoKey: this.selectedLogoKey(),
      includePostgres: dbProfile?.requiresPostgres,
      includeRedis: dbProfile?.requiresRedis,
      parts
    });
  }

  private createProjectWithTargets(): Observable<ProjectDetail> {
    const repo = this.selectedRepo();
    const branch = this.selectedBranch();
    if (!repo || !branch) {
      return throwError(() => new Error('Choose an app and version first.'));
    }

    const targets: { providerName: string; credentialId: string; providerProjectId: string; config?: string }[] = [];

    if (this.publishWebsite) {
      const vercelProjectId = this.selectedVercelProjectId();
      if (!vercelProjectId || !this.selectedVercelCredentialId) {
        return throwError(() => new Error('Vercel setup is incomplete.'));
      }
      targets.push({
        providerName: ProviderName.Vercel,
        credentialId: this.selectedVercelCredentialId,
        providerProjectId: vercelProjectId,
        config: this.buildWebsiteTargetConfig()
      });
    }

    if (this.publishServer) {
      const railwayProjectId = this.selectedRailwayProjectId();
      if (!railwayProjectId || !this.selectedRailwayCredentialId) {
        return throwError(() => new Error('Railway setup is incomplete.'));
      }
      targets.push({
        providerName: ProviderName.Railway,
        credentialId: this.selectedRailwayCredentialId,
        providerProjectId: railwayProjectId,
        config: this.buildServerTargetConfig()
      });
    }

    if (this.publishCoolify) {
      const coolifyProjectId = this.selectedCoolifyProjectId();
      if (!coolifyProjectId || !this.selectedCoolifyCredentialId) {
        return throwError(() => new Error('Coolify setup is incomplete.'));
      }
      targets.push({
        providerName: ProviderName.Coolify,
        credentialId: this.selectedCoolifyCredentialId,
        providerProjectId: coolifyProjectId,
        config: this.buildCoolifyTargetConfig(
          'server',
          this.coolifyProjects().find(project => project.id === coolifyProjectId)?.gitBranch
        )
      });
    }

    if (this.isCoolifyFullStack()) {
      const websiteProjectId = this.selectedCoolifyWebsiteProjectId();
      const serverProjectId = this.selectedCoolifyServerProjectId();
      if (!websiteProjectId || !serverProjectId || !this.selectedCoolifyCredentialId) {
        return throwError(() => new Error('Coolify setup is incomplete.'));
      }
      targets.push({
        providerName: ProviderName.Coolify,
        credentialId: this.selectedCoolifyCredentialId,
        providerProjectId: websiteProjectId,
        config: this.buildWebsiteTargetConfig()
      });
      targets.push({
        providerName: ProviderName.Coolify,
        credentialId: this.selectedCoolifyCredentialId,
        providerProjectId: serverProjectId,
        config: this.buildServerTargetConfig()
      });
    }

    const dbProfile = this.databaseRequirements();
    return this.api.createProject({
      name: this.projectName,
      githubRepoFullName: repo.fullName,
      defaultBranch: branch,
      logoKey: this.selectedLogoKey(),
      includePostgres: dbProfile?.requiresPostgres,
      includeRedis: dbProfile?.requiresRedis,
      targets
    });
  }

  private createVercelProjectRequest(): Observable<{ project: ProviderProject }> {
    const repo = this.selectedRepo();
    if (!repo || !this.selectedVercelCredentialId || !this.newVercelProjectName) {
      return throwError(() => new Error('Missing Vercel connection.'));
    }

    return this.api.createVercelProject(
      this.selectedVercelCredentialId,
      this.newVercelProjectName,
      repo.fullName,
      this.websiteBuildProfile() ?? undefined
    ).pipe(
      map(response => {
        this.selectedVercelProjectId.set(response.project.id);
        return response;
      })
    );
  }

  private createRailwayProjectRequest(): Observable<{ project: ProviderProject }> {
    const repo = this.selectedRepo();
    if (!repo || !this.selectedRailwayCredentialId || !this.newRailwayProjectName) {
      return throwError(() => new Error('Missing Railway connection.'));
    }

    return this.api.createRailwayProject(
      this.selectedRailwayCredentialId,
      this.newRailwayProjectName,
      repo.fullName,
      this.serverBuildProfile() ?? undefined
    ).pipe(
      map(response => {
        this.selectedRailwayProjectId.set(response.project.id);
        return response;
      })
    );
  }

  private createCoolifyProjectRequest(
    role: 'website' | 'server',
    projectName: string
  ): Observable<{ project: ProviderProject }> {
    const repo = this.selectedRepo();
    const branch = this.selectedBranch();
    if (!repo || !branch || !this.selectedCoolifyCredentialId || !projectName) {
      return throwError(() => new Error('Missing Coolify connection.'));
    }

    return this.api.createCoolifyProject(
      this.selectedCoolifyCredentialId,
      projectName,
      repo.fullName,
      branch,
      this.buildCoolifyCreateOptions(repo.private, role)
    ).pipe(
      map(response => {
        if (role === 'website') {
          this.selectedCoolifyWebsiteProjectId.set(response.project.id);
        } else if (this.isCoolifyFullStack()) {
          this.selectedCoolifyServerProjectId.set(response.project.id);
        } else {
          this.selectedCoolifyProjectId.set(response.project.id);
        }
        return response;
      })
    );
  }

  private applyPlanParts(parts: DeploymentPlanPart[]): void {
    const websitePart = parts.find(part => part.role === 'website');
    const serverPart = parts.find(part => part.role === 'server');
    const databaseParts = parts.filter(part => part.role === 'database');
    const isCoolifyFullStack = !!websitePart && !!serverPart &&
      websitePart.providerName === ProviderName.Coolify &&
      serverPart.providerName === ProviderName.Coolify;

    this.publishWebsite = false;
    this.publishServer = false;
    this.publishCoolify = false;

    if (isCoolifyFullStack) {
      this.deploymentMode = 'coolify-fullstack';
    } else if (websitePart && serverPart) {
      this.deploymentMode = 'both';
      this.publishWebsite = true;
      this.publishServer = true;
    } else if (websitePart) {
      this.deploymentMode = 'website';
      this.publishWebsite = true;
    } else if (serverPart) {
      if (serverPart.providerName === ProviderName.Coolify) {
        this.deploymentMode = 'coolify';
        this.publishCoolify = true;
      } else {
        this.deploymentMode = 'server';
        this.publishServer = true;
      }
    }

    if (websitePart) {
      this.websiteRootPath = websitePart.rootDirectory ?? '';
      this.websiteFolderSelected = true;
      this.websiteBuildProfile.set({
        rootDirectory: websitePart.rootDirectory ?? '',
        buildCommand: websitePart.buildCommand ?? 'npm run build',
        installCommand: websitePart.installCommand ?? 'npm install',
        outputDirectory: websitePart.outputDirectory ?? 'dist',
        framework: websitePart.framework
      });
    }

    if (serverPart) {
      const serviceDirectory = serverPart.serviceDirectory ?? serverPart.rootDirectory ?? '';
      this.serverRootPath = serviceDirectory;
      this.serverFolderSelected = true;
      this.serverBuildProfile.set({
        rootDirectory: serverPart.rootDirectory ?? serviceDirectory,
        buildCommand: serverPart.buildCommand,
        installCommand: serverPart.installCommand,
        startCommand: serverPart.startCommand,
        framework: serverPart.framework,
        dockerfilePath: serverPart.dockerfilePath,
        serviceDirectory
      });
    }

    if (databaseParts.length > 0) {
      this.databaseRequirements.set({
        requiresPostgres: databaseParts.some(part => part.databaseEngine === 'postgres'),
        requiresRedis: databaseParts.some(part => part.databaseEngine === 'redis'),
        connectionStringKeys: []
      });
    } else if (serverPart) {
      this.detectDatabaseRequirements(this.serverRootPath);
    }
  }

  setDeploymentMode(mode: DeploymentMode): void {
    this.deploymentMode = mode;
    this.publishWebsite = mode === 'website' || mode === 'both';
    this.publishServer = mode === 'server' || mode === 'both';
    this.publishCoolify = mode === 'coolify';
    if (mode !== 'coolify-fullstack') {
      this.selectedCoolifyWebsiteProjectId.set(null);
      this.selectedCoolifyServerProjectId.set(null);
    }
    if (mode !== 'coolify') {
      this.selectedCoolifyProjectId.set(null);
    }
    if (!this.publishWebsite && !this.isCoolifyFullStack()) {
      this.websiteRootPath = '';
      this.websiteFolderSelected = false;
    }
    if (!this.publishServer && !this.isCoolifyFullStack()) {
      this.serverRootPath = '';
      this.serverFolderSelected = false;
    }
    this.suggestDefaultFolders();
  }

  canContinueFromPartsStep(): boolean {
    if (this.deploymentMode === null) {
      return false;
    }
    if (this.publishCoolify) {
      return true;
    }
    if (this.isCoolifyFullStack()) {
      return this.websiteFolderSelected && this.serverFolderSelected;
    }
    if (this.publishWebsite && !this.websiteFolderSelected) {
      return false;
    }
    if (this.publishServer && !this.serverFolderSelected) {
      return false;
    }
    return true;
  }

  partsStepBlockedHint(): string {
    if (this.deploymentMode === null) {
      return 'Choose what you want to publish.';
    }
    if (this.publishCoolify || this.isCoolifyFullStack()) {
      if (this.isCoolifyFullStack()) {
        if (!this.websiteFolderSelected) {
          return 'Choose the website folder to continue.';
        }
        if (!this.serverFolderSelected) {
          return 'Choose the API folder to continue.';
        }
      }
      return '';
    }
    if (this.publishWebsite && !this.websiteFolderSelected) {
      return 'Choose the website folder to continue.';
    }
    if (this.publishServer && !this.serverFolderSelected) {
      return 'Choose the server folder to continue.';
    }
    return '';
  }

  hostingStepBlockedHint(): string {
    if (this.isCoolifyFullStack()) {
      if (!this.selectedCoolifyWebsiteProjectId()) {
        return 'Select or create a Coolify app for your website.';
      }
      if (!this.selectedCoolifyServerProjectId()) {
        return 'Select or create a Coolify app for your API.';
      }
      return 'Finish hosting setup to continue.';
    }
    if (this.publishWebsite && !this.selectedVercelProjectId()) {
      return 'Select or create a Vercel app to continue.';
    }
    if (this.publishServer && !this.selectedRailwayProjectId()) {
      return 'Select or create a Railway service to continue.';
    }
    if (this.publishCoolify && !this.selectedCoolifyProjectId()) {
      return 'Select or create a Coolify application to continue.';
    }
    return 'Finish hosting setup to continue.';
  }

  canContinueFromHostingStep(): boolean {
    if (this.isCoolifyFullStack()) {
      return !!this.selectedCoolifyWebsiteProjectId() &&
        !!this.selectedCoolifyServerProjectId() &&
        !!this.selectedCoolifyCredentialId;
    }
    if (this.publishWebsite && !this.selectedVercelProjectId()) {
      return false;
    }
    if (this.publishServer && !this.selectedRailwayProjectId()) {
      return false;
    }
    if (this.publishCoolify && !this.selectedCoolifyProjectId()) {
      return false;
    }
    return this.publishWebsite || this.publishServer || this.publishCoolify || this.isCoolifyFullStack();
  }

  enterHostingStep(): void {
    this.step.set(4);
    if (this.publishWebsite && !this.isCoolifyFullStack() && this.vercelCredentials().length > 0) {
      this.loadVercelProjects();
    }
    if (this.publishServer && !this.isCoolifyFullStack() && this.railwayCredentials().length > 0) {
      this.loadRailwayProjects();
    }
    if ((this.publishCoolify || this.isCoolifyFullStack()) && this.coolifyCredentials().length > 0) {
      this.loadCoolifyProjects();
      if (this.publishCoolify && this.coolifyHostingMode() === 'create') {
        this.loadCoolifyInfrastructure();
      }
      if (this.isCoolifyFullStack() &&
          (this.coolifyWebsiteHostingMode() === 'create' || this.coolifyServerHostingMode() === 'create')) {
        this.loadCoolifyInfrastructure();
      }
    }
  }

  setCoolifyHostingMode(mode: 'existing' | 'create'): void {
    this.coolifyHostingMode.set(mode);
    this.selectedCoolifyProjectId.set(null);
    if (mode === 'existing') {
      this.loadCoolifyProjects();
    } else {
      this.loadCoolifyInfrastructure();
    }
  }

  setCoolifyWebsiteHostingMode(mode: 'existing' | 'create'): void {
    this.coolifyWebsiteHostingMode.set(mode);
    this.selectedCoolifyWebsiteProjectId.set(null);
    if (mode === 'existing') {
      this.loadCoolifyProjects();
    } else {
      this.loadCoolifyInfrastructure();
    }
  }

  setCoolifyServerHostingMode(mode: 'existing' | 'create'): void {
    this.coolifyServerHostingMode.set(mode);
    this.selectedCoolifyServerProjectId.set(null);
    if (mode === 'existing') {
      this.loadCoolifyProjects();
    } else {
      this.loadCoolifyInfrastructure();
    }
  }

  setVercelHostingMode(mode: 'existing' | 'create'): void {
    this.vercelHostingMode.set(mode);
    this.selectedVercelProjectId.set(null);
    if (mode === 'existing') {
      this.loadVercelProjects();
    }
  }

  setRailwayHostingMode(mode: 'existing' | 'create'): void {
    this.railwayHostingMode.set(mode);
    this.selectedRailwayProjectId.set(null);
    if (mode === 'existing') {
      this.loadRailwayProjects();
    }
  }

  onVercelCredentialChange(): void {
    this.selectedVercelProjectId.set(null);
    if (this.vercelHostingMode() === 'existing') {
      this.loadVercelProjects();
    }
  }

  onRailwayCredentialChange(): void {
    this.selectedRailwayProjectId.set(null);
    if (this.railwayHostingMode() === 'existing') {
      this.loadRailwayProjects();
    }
  }

  onCoolifyCredentialChange(): void {
    this.selectedCoolifyProjectId.set(null);
    this.selectedCoolifyWebsiteProjectId.set(null);
    this.selectedCoolifyServerProjectId.set(null);
    this.selectedCoolifyGithubAppId = '';
    if (this.publishCoolify && this.coolifyHostingMode() === 'existing') {
      this.loadCoolifyProjects();
    } else if (this.isCoolifyFullStack()) {
      if (this.coolifyWebsiteHostingMode() === 'existing' || this.coolifyServerHostingMode() === 'existing') {
        this.loadCoolifyProjects();
      }
      if (this.coolifyWebsiteHostingMode() === 'create' || this.coolifyServerHostingMode() === 'create') {
        this.loadCoolifyInfrastructure();
      }
    } else if (this.coolifyHostingMode() === 'existing') {
      this.loadCoolifyProjects();
    } else {
      this.loadCoolifyInfrastructure();
    }
  }

  detectedDatabaseSummary(): string {
    const profile = this.databaseRequirements();
    if (!profile || (!profile.requiresPostgres && !profile.requiresRedis)) {
      return '';
    }

    const parts: string[] = [];
    if (profile.requiresPostgres) {
      parts.push('PostgreSQL');
    }
    if (profile.requiresRedis) {
      parts.push('Redis');
    }

    const host = this.isCoolifyFullStack() || this.publishCoolify ? 'Coolify' : 'Railway';
    return `DeployAI will create ${parts.join(' and ')} on ${host} automatically.`;
  }

  loadVercelProjects(): void {
    if (!this.selectedVercelCredentialId) return;
    this.loadingVercelProjects.set(true);
    this.selectedVercelProjectId.set(null);
    this.api.listProviderProjects(this.selectedVercelCredentialId).subscribe({
      next: (response) => {
        this.vercelProjects.set(response.projects);
        this.loadingVercelProjects.set(false);
      },
      error: (err) => {
        this.loadingVercelProjects.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not load your Vercel apps.');
      }
    });
  }

  loadRailwayProjects(): void {
    if (!this.selectedRailwayCredentialId) return;
    this.loadingRailwayProjects.set(true);
    this.selectedRailwayProjectId.set(null);
    this.api.listProviderProjects(this.selectedRailwayCredentialId).subscribe({
      next: (response) => {
        this.railwayProjects.set(response.projects);
        this.loadingRailwayProjects.set(false);
      },
      error: (err) => {
        this.loadingRailwayProjects.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not load your Railway services.');
      }
    });
  }

  loadCoolifyProjects(): void {
    if (!this.selectedCoolifyCredentialId) return;
    this.loadingCoolifyProjects.set(true);
    this.api.listProviderProjects(this.selectedCoolifyCredentialId).subscribe({
      next: (response) => {
        this.coolifyProjects.set(response.projects);
        this.loadingCoolifyProjects.set(false);
      },
      error: (err) => {
        this.loadingCoolifyProjects.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not load your Coolify applications.');
      }
    });
  }

  createVercelProject(): void {
    const repo = this.selectedRepo();
    if (!repo || !this.selectedVercelCredentialId || !this.newVercelProjectName) return;

    this.creatingVercelProject.set(true);
    this.error.set(null);
    this.api.createVercelProject(
      this.selectedVercelCredentialId,
      this.newVercelProjectName,
      repo.fullName,
      this.websiteBuildProfile() ?? undefined
    ).subscribe({
      next: (response) => {
        this.selectedVercelProjectId.set(response.project.id);
        this.vercelProjects.update(projects => [...projects, response.project]);
        this.creatingVercelProject.set(false);
      },
      error: (err) => {
        this.creatingVercelProject.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not create that Vercel app.');
      }
    });
  }

  createRailwayProject(): void {
    const repo = this.selectedRepo();
    if (!repo || !this.selectedRailwayCredentialId || !this.newRailwayProjectName) return;

    this.creatingRailwayProject.set(true);
    this.error.set(null);
    this.api.createRailwayProject(
      this.selectedRailwayCredentialId,
      this.newRailwayProjectName,
      repo.fullName,
      this.serverBuildProfile() ?? undefined
    ).subscribe({
      next: (response) => {
        this.selectedRailwayProjectId.set(response.project.id);
        this.railwayProjects.update(projects => [...projects, response.project]);
        this.railwayHostingMode.set('existing');
        this.creatingRailwayProject.set(false);
        this.error.set(null);
      },
      error: (err) => {
        this.creatingRailwayProject.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not create that Railway service.');
        this.loadRailwayProjects();
      }
    });
  }

  createCoolifyProject(): void {
    this.createCoolifyProjectForRole('server', this.newCoolifyProjectName, id => {
      this.selectedCoolifyProjectId.set(id);
    });
  }

  createCoolifyWebsiteProject(): void {
    this.createCoolifyProjectForRole('website', this.newCoolifyWebsiteProjectName, id => {
      this.selectedCoolifyWebsiteProjectId.set(id);
    });
  }

  createCoolifyServerProject(): void {
    this.createCoolifyProjectForRole('server', this.newCoolifyServerProjectName, id => {
      this.selectedCoolifyServerProjectId.set(id);
    });
  }

  private createCoolifyProjectForRole(
    role: 'website' | 'server',
    projectName: string,
    selectProject: (id: string) => void
  ): void {
    const repo = this.selectedRepo();
    const branch = this.selectedBranch();
    if (!repo || !branch || !this.selectedCoolifyCredentialId || !projectName) return;

    this.creatingCoolifyProject.set(true);
    this.error.set(null);
    this.api.createCoolifyProject(
      this.selectedCoolifyCredentialId,
      projectName,
      repo.fullName,
      branch,
      this.buildCoolifyCreateOptions(repo.private, role)
    ).subscribe({
      next: (response) => {
        selectProject(response.project.id);
        this.coolifyProjects.update(projects => [...projects, response.project]);
        if (role === 'website') {
          this.coolifyWebsiteHostingMode.set('existing');
        } else if (this.isCoolifyFullStack()) {
          this.coolifyServerHostingMode.set('existing');
        } else {
          this.coolifyHostingMode.set('existing');
        }
        this.creatingCoolifyProject.set(false);
        this.error.set(null);
      },
      error: (err) => {
        this.creatingCoolifyProject.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not create that Coolify application.');
        this.loadCoolifyProjects();
      }
    });
  }

  loadCoolifyInfrastructure(): void {
    if (!this.selectedCoolifyCredentialId) return;
    this.loadingCoolifyInfrastructure.set(true);
    this.api.listCoolifyInfrastructure(this.selectedCoolifyCredentialId).subscribe({
      next: (response) => {
        this.coolifyInfrastructure.set(response);
        if (!this.selectedCoolifyProjectUuid) {
          this.selectedCoolifyProjectUuid = this.pickCoolifyProject(response.projects);
        }
        if (!this.selectedCoolifyServerUuid && response.servers.length > 0) {
          this.selectedCoolifyServerUuid = response.servers[0].id;
        }
        if (!this.selectedCoolifyGithubAppId && response.githubApps.length === 1) {
          this.selectedCoolifyGithubAppId = response.githubApps[0].id;
        }
        this.loadingCoolifyInfrastructure.set(false);
        this.loadCoolifyEnvironments();
      },
      error: (err) => {
        this.loadingCoolifyInfrastructure.set(false);
        this.coolifyEnvironments.set([]);
        this.error.set(err?.error?.error?.message ?? 'Could not load your Coolify infrastructure.');
      }
    });
  }

  /**
   * Taking projects[0] drops the app into whichever project Coolify listed first — for a repo
   * called `yemeni-breeze` on an instance that also hosts `tickethub-coolify`, that is the wrong
   * one and nothing says so. Prefer a project named after the repo, and pick nothing at all when
   * there are several and none match, so the choice stays visible instead of being guessed.
   */
  private pickCoolifyProject(projects: { id: string; name: string }[]): string {
    if (projects.length === 0) return '';
    if (projects.length === 1) return projects[0].id;

    const repoName = (this.selectedRepo()?.fullName ?? '').split('/').pop()?.toLowerCase() ?? '';
    const byName = projects.find(p => p.name.toLowerCase() === repoName);
    return byName?.id ?? '';
  }

  /**
   * The compose file the plan deploys. Matches what the generated templates emit; the plain
   * docker-compose.yml is usually a repo's local dev stack, so it is not the default here.
   */
  readonly composeFileLocation = 'docker-compose.coolify.yml';

  /** Domain to attach to the web service. Blank means let the server autogenerate one. */
  customDomain = '';

  // ---- Env collection step (compose plans) -------------------------------------------------
  readonly showEnvStep = signal(false);
  readonly loadingEnvSchema = signal(false);
  readonly envSchema = signal<EnvSchemaVar[]>([]);
  /** Working values keyed by var name; prefilled from suggestions/defaults, user-editable. */
  envValues: Record<string, string> = {};

  requiredEnvVars(): EnvSchemaVar[] {
    return this.envSchema().filter(v => !v.hasDefault);
  }

  optionalEnvVars(): EnvSchemaVar[] {
    return this.envSchema().filter(v => v.hasDefault);
  }

  missingRequiredEnv(): string[] {
    return this.requiredEnvVars()
      .filter(v => !(this.envValues[v.name] ?? '').trim())
      .map(v => v.name);
  }

  regenerateSecret(name: string): void {
    const upper = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
    const lower = 'abcdefghijklmnopqrstuvwxyz';
    const digits = '0123456789';
    // Passwords need a symbol too — ASP.NET Identity's default policy rejects
    // alphanumeric-only values, and a generated password must boot the app.
    // Symbol set avoids $ # quotes and backslash so .env interpolation can't mangle it.
    const symbols = '!@^*-_+=?.';
    const isPassword = name.toUpperCase().includes('PASSWORD');
    const alphabet = upper + lower + digits + (isPassword ? symbols : '');
    const length = isPassword ? 20 : 48;
    const bytes = new Uint8Array(length + 4);
    crypto.getRandomValues(bytes);
    const chars = Array.from(bytes.slice(0, length), b => alphabet[b % alphabet.length]);
    if (isPassword) {
      // Guarantee one of each class at distinct positions.
      const classes = [upper, lower, digits, symbols];
      const used = new Set<number>();
      classes.forEach((set, c) => {
        let pos = bytes[length + c] % length;
        while (used.has(pos)) {
          pos = (pos + 1) % length;
        }
        used.add(pos);
        chars[pos] = set[bytes[length + c] % set.length];
      });
    }
    this.envValues[name] = chars.join('');
  }

  private loadEnvSchema(): void {
    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    this.loadingEnvSchema.set(true);
    const serverPart = this.activePlanParts().find(p => p.role === 'server');
    this.api.getEnvSchema(owner, name, branch, serverPart?.serviceDirectory ?? serverPart?.rootDirectory ?? undefined)
      .subscribe({
        next: (response) => {
          this.envSchema.set(response.vars);
          for (const variable of response.vars) {
            if (this.envValues[variable.name]) {
              continue; // don't clobber what the user already typed
            }

            // Smart-fill order: the domain the user chose beats any server suggestion for
            // domain-category vars; otherwise suggestion, then declared default.
            if (variable.category === 'domain' && this.customDomain.trim()) {
              this.envValues[variable.name] = `https://${this.customDomain.trim().replace(/^https?:\/\//, '')}`;
            } else if (variable.suggestedValue) {
              this.envValues[variable.name] = variable.suggestedValue;
            } else if (variable.defaultValue) {
              this.envValues[variable.name] = variable.defaultValue;
            }
          }
          this.loadingEnvSchema.set(false);
        },
        error: () => {
          // Detection failing must not block deploying — the step just shows nothing to fill.
          this.envSchema.set([]);
          this.loadingEnvSchema.set(false);
        }
      });
  }

  backToPlanFromEnv(): void {
    this.showEnvStep.set(false);
  }

  confirmEnvAndDeploy(): void {
    if (this.missingRequiredEnv().length > 0) {
      this.error.set(`Fill in: ${this.missingRequiredEnv().join(', ')}`);
      return;
    }

    this.error.set(null);
    this.deployFromPlan();
  }

  isSingleOriginComposePlan(): boolean {
    return this.deploymentPlan()?.planKind === DeploymentPlanKind.CoolifyCompose;
  }

  /** Projects on the Coolify server — the choice of where an app lands. */
  coolifyDestinations(): { id: string; name: string }[] {
    return this.coolifyInfrastructure()?.projects ?? [];
  }

  private ensureCoolifyDestinations(): void {
    const coolify = this.coolifyCredentials()[0];
    if (!coolify) {
      return;
    }

    if (!this.selectedCoolifyCredentialId) {
      this.selectedCoolifyCredentialId = coolify.id;
    }

    if (this.coolifyDestinations().length === 0) {
      this.loadCoolifyInfrastructure();
    }
  }

  onPlanDestinationChange(projectUuid: string): void {
    this.selectedCoolifyProjectUuid = projectUuid;
    this.onCoolifyProjectUuidChange();
  }

  onCoolifyProjectUuidChange(): void {
    this.selectedCoolifyEnvironmentName = '';
    this.loadCoolifyEnvironments();
  }

  loadCoolifyEnvironments(): void {
    if (!this.selectedCoolifyCredentialId || !this.selectedCoolifyProjectUuid) {
      this.coolifyEnvironments.set([]);
      return;
    }

    this.loadingCoolifyEnvironments.set(true);
    this.api.listCoolifyProjectEnvironments(
      this.selectedCoolifyCredentialId,
      this.selectedCoolifyProjectUuid
    ).subscribe({
      next: (response) => {
        this.coolifyEnvironments.set(response.environments);
        if (!this.selectedCoolifyEnvironmentName && response.environments.length > 0) {
          this.selectedCoolifyEnvironmentName = response.environments[0].name;
        }
        this.loadingCoolifyEnvironments.set(false);
      },
      error: () => {
        this.coolifyEnvironments.set([]);
        this.loadingCoolifyEnvironments.set(false);
      }
    });
  }

  coolifyCreateBlockedReason(): string | null {
    const repo = this.selectedRepo();
    if (repo?.private && !this.selectedCoolifyGithubAppId) {
      const apps = this.coolifyInfrastructure()?.githubApps ?? [];
      if (apps.length === 0) {
        return 'Connect a GitHub App in Coolify to deploy private repositories.';
      }
      return 'Choose the Coolify GitHub App for this private repository.';
    }
    return null;
  }

  private isServerRenderedFramework(framework?: string): boolean {
    const normalized = framework?.trim().toLowerCase();
    return normalized === 'next' || normalized === 'nextjs' || normalized === 'nuxt'
      || normalized === 'sveltekit' || normalized === 'remix';
  }

  private buildCoolifyCreateOptions(isPrivateRepository: boolean, role: 'website' | 'server' = 'server') {
    const serverProfile = role === 'server' ? this.serverBuildProfile() : null;
    const websiteProfile = role === 'website' ? this.websiteBuildProfile() : null;
    const profile = serverProfile ?? websiteProfile ?? undefined;
    const dockerfilePath = serverProfile?.dockerfilePath;
    const outputDirectory = websiteProfile?.outputDirectory;

    // A single-origin plan is one compose resource, not one app per role. Sending the
    // per-role build settings here produced a Dockerfile app with a base directory instead,
    // which Coolify rejected outright.
    if (this.isSingleOriginComposePlan()) {
      return {
        isPrivateRepository,
        coolifyProjectUuid: this.selectedCoolifyProjectUuid || undefined,
        coolifyServerUuid: this.selectedCoolifyServerUuid || undefined,
        coolifyEnvironmentName: this.selectedCoolifyEnvironmentName || undefined,
        coolifyGithubAppUuid: this.selectedCoolifyGithubAppId || undefined,
        buildPack: CoolifyBuildPack.DockerCompose,
        composeFileLocation: this.composeFileLocation,
        customDomain: this.customDomain.trim() || undefined,
        // The service that faces the browser; the api service stays on the internal network.
        domainServiceName: 'web',
        framework: profile?.framework
      };
    }

    return {
      isPrivateRepository,
      coolifyProjectUuid: this.selectedCoolifyProjectUuid || undefined,
      coolifyServerUuid: this.selectedCoolifyServerUuid || undefined,
      coolifyEnvironmentName: this.selectedCoolifyEnvironmentName || undefined,
      coolifyGithubAppUuid: this.selectedCoolifyGithubAppId || undefined,
      // An SSR framework (Next, Nuxt, SvelteKit, Remix) has an output dir (.next, .output,
      // build) but ships a Node server, so it builds with Nixpacks — serving that output as
      // static files renders a blank page. Only a genuinely static bundle uses Static.
      buildPack: dockerfilePath || profile?.framework === 'docker'
        ? CoolifyBuildPack.Dockerfile
        : outputDirectory && !this.isServerRenderedFramework(profile?.framework)
          ? CoolifyBuildPack.Static
          : CoolifyBuildPack.Nixpacks,
      rootDirectory: profile?.rootDirectory,
      outputDirectory,
      buildCommand: profile?.buildCommand,
      installCommand: profile?.installCommand,
      startCommand: serverProfile?.startCommand,
      framework: profile?.framework,
      dockerfilePath,
      serviceDirectory: serverProfile?.serviceDirectory
    };
  }

  selectedCoolifyProjectName(): string | null {
    const projectId = this.selectedCoolifyProjectId();
    if (!projectId) {
      return null;
    }

    return this.coolifyProjects().find(project => project.id === projectId)?.name ?? projectId;
  }

  selectedCoolifyWebsiteProjectName(): string | null {
    const projectId = this.selectedCoolifyWebsiteProjectId();
    if (!projectId) {
      return null;
    }

    return this.coolifyProjects().find(project => project.id === projectId)?.name ?? projectId;
  }

  selectedCoolifyServerProjectName(): string | null {
    const projectId = this.selectedCoolifyServerProjectId();
    if (!projectId) {
      return null;
    }

    return this.coolifyProjects().find(project => project.id === projectId)?.name ?? projectId;
  }

  private buildCoolifyTargetConfig(
    role: 'website' | 'server',
    gitBranch?: string,
    buildPack?: CoolifyBuildPack
  ): string {
    return JSON.stringify({
      role,
      coolifyGitBranch: gitBranch ?? undefined,
      coolifyBuildPack: buildPack ?? undefined
    });
  }

  addEnvRow(): void {
    this.envRows.push({ key: '', value: '' });
  }

  create(): void {
    const repo = this.selectedRepo();
    const branch = this.selectedBranch();
    if (!repo || !branch) return;

    const targets: { providerName: string; credentialId: string; providerProjectId: string; config?: string }[] = [];

    if (this.publishWebsite) {
      const vercelProjectId = this.selectedVercelProjectId();
      if (!vercelProjectId || !this.selectedVercelCredentialId) return;
      targets.push({
        providerName: ProviderName.Vercel,
        credentialId: this.selectedVercelCredentialId,
        providerProjectId: vercelProjectId,
        config: this.buildWebsiteTargetConfig()
      });
    }

    if (this.publishServer) {
      const railwayProjectId = this.selectedRailwayProjectId();
      if (!railwayProjectId || !this.selectedRailwayCredentialId) return;
      targets.push({
        providerName: ProviderName.Railway,
        credentialId: this.selectedRailwayCredentialId,
        providerProjectId: railwayProjectId,
        config: this.buildServerTargetConfig()
      });
    }

    if (this.publishCoolify) {
      const coolifyProjectId = this.selectedCoolifyProjectId();
      if (!coolifyProjectId || !this.selectedCoolifyCredentialId) return;
      targets.push({
        providerName: ProviderName.Coolify,
        credentialId: this.selectedCoolifyCredentialId,
        providerProjectId: coolifyProjectId,
        config: this.buildCoolifyTargetConfig(
          'server',
          this.coolifyProjects().find(project => project.id === coolifyProjectId)?.gitBranch
        )
      });
    }

    if (this.isCoolifyFullStack()) {
      const websiteProjectId = this.selectedCoolifyWebsiteProjectId();
      const serverProjectId = this.selectedCoolifyServerProjectId();
      if (!websiteProjectId || !serverProjectId || !this.selectedCoolifyCredentialId) return;
      targets.push({
        providerName: ProviderName.Coolify,
        credentialId: this.selectedCoolifyCredentialId,
        providerProjectId: websiteProjectId,
        config: this.buildWebsiteTargetConfig()
      });
      targets.push({
        providerName: ProviderName.Coolify,
        credentialId: this.selectedCoolifyCredentialId,
        providerProjectId: serverProjectId,
        config: this.buildServerTargetConfig()
      });
    }

    const dbProfile = this.databaseRequirements();
    this.saving.set(true);
    this.api.createProject({
      name: this.projectName,
      githubRepoFullName: repo.fullName,
      defaultBranch: branch,
      logoKey: this.selectedLogoKey(),
      includePostgres: dbProfile?.requiresPostgres,
      includeRedis: dbProfile?.requiresRedis,
      targets
    }).subscribe({
      next: (project) => {
        const envRows = this.envRows.filter(r => r.key && r.value);
        const vercelProjectId = this.selectedVercelProjectId();
        if (!this.publishWebsite || envRows.length === 0 || !vercelProjectId) {
          void this.router.navigate(['/projects', project.id]);
          return;
        }

        let completed = 0;
        for (const row of envRows) {
          this.api.upsertProjectEnvVar(this.selectedVercelCredentialId, vercelProjectId, {
            key: row.key,
            value: row.value,
            type: 'encrypted'
          }).subscribe({
            next: () => {
              completed++;
              if (completed === envRows.length) {
                void this.router.navigate(['/projects', project.id]);
              }
            },
            error: () => void this.router.navigate(['/projects', project.id])
          });
        }
      },
      error: (err) => {
        this.error.set(err?.error?.error?.message ?? 'Could not save that app.');
        this.saving.set(false);
      }
    });
  }

  onWebsiteFolderSelected(path: string): void {
    this.websiteRootPath = path;
    this.websiteFolderSelected = true;
    this.detectWebsiteBuildProfile(path);
  }

  private detectWebsiteBuildProfile(path: string): void {
    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    this.detectingWebsiteProfile.set(true);
    this.api.detectBuildProfile(owner, name, path, branch).subscribe({
      next: (profile) => {
        this.websiteBuildProfile.set(profile);
        this.detectingWebsiteProfile.set(false);
      },
      error: () => {
        this.websiteBuildProfile.set(null);
        this.detectingWebsiteProfile.set(false);
      }
    });
  }

  private buildWebsiteTargetConfig(): string {
    const profile = this.websiteBuildProfile();
    const config: Record<string, string> = {
      rootDirectory: profile?.rootDirectory ?? this.websiteRootPath,
      role: 'website'
    };

    if (profile?.outputDirectory) {
      config['outputDirectory'] = profile.outputDirectory;
    }
    if (profile?.buildCommand) {
      config['buildCommand'] = profile.buildCommand;
    }
    if (profile?.installCommand) {
      config['installCommand'] = profile.installCommand;
    }
    if (profile?.framework) {
      config['framework'] = profile.framework;
    }

    return JSON.stringify(config);
  }

  onServerFolderSelected(path: string): void {
    this.serverRootPath = path;
    this.serverFolderSelected = true;
    this.detectServerBuildProfile(path);
    this.detectDatabaseRequirements(path);
  }

  private detectDatabaseRequirements(path: string): void {
    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    this.detectingDatabaseRequirements.set(true);
    this.api.detectDatabaseRequirements(owner, name, path, branch).subscribe({
      next: (profile) => {
        this.databaseRequirements.set(profile);
        this.detectingDatabaseRequirements.set(false);
      },
      error: () => {
        this.databaseRequirements.set(null);
        this.detectingDatabaseRequirements.set(false);
      }
    });
  }

  private detectServerBuildProfile(path: string): void {
    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    this.detectingServerProfile.set(true);
    this.api.detectServerBuildProfile(owner, name, path, branch).subscribe({
      next: (profile) => {
        this.serverBuildProfile.set(profile);
        this.detectingServerProfile.set(false);
      },
      error: () => {
        this.serverBuildProfile.set(null);
        this.detectingServerProfile.set(false);
      }
    });
  }

  private buildServerTargetConfig(): string {
    const profile = this.serverBuildProfile();
    const serviceDirectory = profile?.serviceDirectory ?? this.serverRootPath;
    const config: Record<string, string | boolean> = {
      rootDirectory: profile?.rootDirectory ?? serviceDirectory,
      serviceDirectory,
      role: 'server'
    };

    if (profile?.buildCommand) {
      config['buildCommand'] = profile.buildCommand;
    }
    if (profile?.installCommand) {
      config['installCommand'] = profile.installCommand;
    }
    if (profile?.startCommand) {
      config['startCommand'] = profile.startCommand;
    }
    if (profile?.framework) {
      config['framework'] = profile.framework;
    }
    if (profile?.dockerfilePath) {
      config['dockerfilePath'] = profile.dockerfilePath;
    }

    return JSON.stringify(config);
  }

  private suggestDefaultFolders(): void {
    if (!this.publishWebsite && !this.publishServer && !this.isCoolifyFullStack()) {
      return;
    }

    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    if (this.publishWebsite || this.isCoolifyFullStack()) {
      this.api.listRepoContents(owner, name, '', branch).subscribe({
        next: (response) => {
          const names = new Set(response.directories.map(d => d.name.toLowerCase()));
          if (names.has('client')) {
            this.onWebsiteFolderSelected('client');
          }
        }
      });
    }

    if (this.publishServer || this.isCoolifyFullStack()) {
      this.api.detectServerBuildProfile(owner, name, '', branch).subscribe({
        next: (profile) => {
          if (profile.serviceDirectory || profile.rootDirectory) {
            this.onServerFolderSelected(profile.serviceDirectory ?? profile.rootDirectory);
          }
        }
      });
    }
  }
}
