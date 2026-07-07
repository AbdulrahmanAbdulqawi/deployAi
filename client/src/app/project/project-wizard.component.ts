import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { forkJoin, map, Observable, of, switchMap, throwError } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import {
  CredentialSummary,
  DatabaseRequirementProfile,
  DeploymentPlan,
  DeploymentPlanPart,
  DeploymentReadinessResult,
  FrontendBuildProfile,
  ProjectDetail,
  ServerBuildProfile,
  GitHubBranch,
  GitHubRepo,
  ProviderProject
} from '../core/models/api.models';
import { RepoFolderPickerComponent } from '../shared/repo-folder-picker/repo-folder-picker.component';
import { DeployPlanComponent } from '../shared/deploy-plan/deploy-plan.component';
import { DeploymentSetupPanelComponent } from '../shared/deployment-setup-panel/deployment-setup-panel.component';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { ProjectsStore } from '../core/stores/projects.store';

interface EnvRow {
  key: string;
  value: string;
}

type DeploymentMode = 'website' | 'server' | 'both';

@Component({
  selector: 'app-project-wizard',
  standalone: true,
  imports: [FormsModule, RepoFolderPickerComponent, DeployPlanComponent, DeploymentSetupPanelComponent, ButtonComponent, IconComponent],
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
  readonly selectedRepo = signal<GitHubRepo | null>(null);
  readonly selectedBranch = signal<string | null>(null);
  readonly selectedVercelProjectId = signal<string | null>(null);
  readonly selectedRailwayProjectId = signal<string | null>(null);
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
  newVercelProjectName = '';
  newRailwayProjectName = '';
  selectedVercelCredentialId = '';
  selectedRailwayCredentialId = '';
  deploymentMode: DeploymentMode | null = null;
  publishWebsite = false;
  publishServer = false;
  websiteRootPath = '';
  serverRootPath = '';
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
        const vercel = response.credentials.find(c => c.providerName === 'vercel');
        if (vercel) {
          this.selectedVercelCredentialId = vercel.id;
          this.loadVercelProjects();
        }
        const railway = response.credentials.find(c => c.providerName === 'railway');
        if (railway) {
          this.selectedRailwayCredentialId = railway.id;
          this.loadRailwayProjects();
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

  missingConnections(): string[] {
    const missing: string[] = [];
    if (this.publishWebsite && this.vercelCredentials().length === 0) {
      missing.push('Vercel');
    }
    if (this.publishServer && this.railwayCredentials().length === 0) {
      missing.push('Railway');
    }
    return missing;
  }

  vercelCredentials(): CredentialSummary[] {
    return this.credentials().filter(c => c.providerName === 'vercel');
  }

  railwayCredentials(): CredentialSummary[] {
    return this.credentials().filter(c => c.providerName === 'railway');
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
    this.newVercelProjectName = this.sanitizeName(repoName);
    this.newRailwayProjectName = `${this.sanitizeName(repoName)}-api`;
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
    this.websiteRootPath = '';
    this.serverRootPath = '';
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

  showDeploymentSetupPanel(readiness: DeploymentReadinessResult): boolean {
    return readiness.usesSplitOrigin;
  }

  deployFromPlan(): void {
    if (this.missingConnections().length > 0) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    this.ensureProviderProjects().pipe(
      switchMap(() => this.createProjectFromPlanRequest()),
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

  private ensureProviderProjects(): Observable<void> {
    const tasks: Observable<unknown>[] = [];

    if (this.publishWebsite && !this.selectedVercelProjectId()) {
      tasks.push(this.createVercelProjectRequest());
    }
    if (this.publishServer && !this.selectedRailwayProjectId()) {
      tasks.push(this.createRailwayProjectRequest());
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
        const credentialId = isWebsite ? this.selectedVercelCredentialId : this.selectedRailwayCredentialId;
        const providerProjectId = isWebsite ? this.selectedVercelProjectId() : this.selectedRailwayProjectId();
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
        providerName: 'vercel',
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
        providerName: 'railway',
        credentialId: this.selectedRailwayCredentialId,
        providerProjectId: railwayProjectId,
        config: this.buildServerTargetConfig()
      });
    }

    const dbProfile = this.databaseRequirements();
    return this.api.createProject({
      name: this.projectName,
      githubRepoFullName: repo.fullName,
      defaultBranch: branch,
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

  private applyPlanParts(parts: DeploymentPlanPart[]): void {
    const websitePart = parts.find(part => part.role === 'website');
    const serverPart = parts.find(part => part.role === 'server');
    const databaseParts = parts.filter(part => part.role === 'database');

    if (websitePart && serverPart) {
      this.deploymentMode = 'both';
      this.publishWebsite = true;
      this.publishServer = true;
    } else if (websitePart) {
      this.deploymentMode = 'website';
      this.publishWebsite = true;
      this.publishServer = false;
    } else if (serverPart) {
      this.deploymentMode = 'server';
      this.publishWebsite = false;
      this.publishServer = true;
    }

    if (websitePart) {
      this.websiteRootPath = websitePart.rootDirectory ?? '';
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
    this.suggestDefaultFolders();
  }

  canContinueFromPartsStep(): boolean {
    if (this.deploymentMode === null) {
      return false;
    }
    if (this.publishWebsite && !this.websiteRootPath.trim()) {
      return false;
    }
    if (this.publishServer && !this.serverRootPath.trim()) {
      return false;
    }
    return true;
  }

  partsStepBlockedHint(): string {
    if (this.deploymentMode === null) {
      return 'Choose what you want to publish.';
    }
    if (this.publishWebsite && !this.websiteRootPath.trim()) {
      return 'Choose the website folder to continue.';
    }
    if (this.publishServer && !this.serverRootPath.trim()) {
      return 'Choose the server folder to continue.';
    }
    return '';
  }

  hostingStepBlockedHint(): string {
    if (this.publishWebsite && !this.selectedVercelProjectId()) {
      return 'Select or create a Vercel app to continue.';
    }
    if (this.publishServer && !this.selectedRailwayProjectId()) {
      return 'Select or create a Railway service to continue.';
    }
    return 'Finish hosting setup to continue.';
  }

  enterHostingStep(): void {
    this.step.set(4);
    if (this.publishWebsite && this.vercelCredentials().length > 0) {
      this.loadVercelProjects();
    }
    if (this.publishServer && this.railwayCredentials().length > 0) {
      this.loadRailwayProjects();
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

  canContinueFromHostingStep(): boolean {
    if (this.publishWebsite && !this.selectedVercelProjectId()) {
      return false;
    }
    if (this.publishServer && !this.selectedRailwayProjectId()) {
      return false;
    }
    return this.publishWebsite || this.publishServer;
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

    return `DeployAI will create ${parts.join(' and ')} on Railway automatically.`;
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
        providerName: 'vercel',
        credentialId: this.selectedVercelCredentialId,
        providerProjectId: vercelProjectId,
        config: this.buildWebsiteTargetConfig()
      });
    }

    if (this.publishServer) {
      const railwayProjectId = this.selectedRailwayProjectId();
      if (!railwayProjectId || !this.selectedRailwayCredentialId) return;
      targets.push({
        providerName: 'railway',
        credentialId: this.selectedRailwayCredentialId,
        providerProjectId: railwayProjectId,
        config: this.buildServerTargetConfig()
      });
    }

    this.saving.set(true);
    this.api.createProject({
      name: this.projectName,
      githubRepoFullName: repo.fullName,
      defaultBranch: branch,
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
    if (!this.publishWebsite && !this.publishServer) {
      return;
    }

    const owner = this.repoOwner();
    const name = this.repoName();
    const branch = this.selectedBranch();
    if (!owner || !name || !branch) {
      return;
    }

    if (this.publishWebsite) {
      this.api.listRepoContents(owner, name, '', branch).subscribe({
        next: (response) => {
          const names = new Set(response.directories.map(d => d.name.toLowerCase()));
          if (names.has('client')) {
            this.onWebsiteFolderSelected('client');
          }
        }
      });
    }

    if (this.publishServer) {
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
