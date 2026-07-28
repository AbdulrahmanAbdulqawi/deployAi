import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { AiSetupPreferenceService } from '../core/services/ai-setup-preference.service';
import { ProjectsStore } from '../core/stores/projects.store';
import {
  DatabaseRequirementProfile,
  DataServiceInfo,
  DeploymentReadinessResult,
  DeploymentSummary,
  ProjectDetail,
  ProjectServiceStatus,
  ProjectServiceView,
  ProjectServicesResponse,
  ProviderEnvVar,
  ProviderName,
  EnvVariable,
  UnreadableEnvTarget,
  MissingConfigurationItem
} from '../core/models/api.models';
import {
  databaseEngineLabel,
  parseTargetConfig,
  providerLabel,
  serviceStatusLabel
} from '../core/utils/target-config';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { IconComponent, IconName } from '../shared/ui/icon/icon.component';
import { DropdownMenuComponent, DropdownMenuItem } from '../shared/ui/dropdown/dropdown-menu.component';
import { DeploymentStatusStripComponent } from '../shared/deployment-status-strip/deployment-status-strip.component';
import { ReadinessScorecardComponent } from '../shared/readiness-scorecard/readiness-scorecard.component';
import { EnvironmentDriftBannerComponent } from '../shared/environment-drift-banner/environment-drift-banner.component';
import { ProjectHealthBannerComponent } from '../shared/project-health-banner/project-health-banner.component';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { ConfirmService } from '../shared/ui/confirm/confirm.service';
import { ToastService } from '../shared/ui/toast/toast.service';

interface LiveUrl {
  label: string;
  url: string;
}

@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [
    FormsModule,
    ButtonComponent,
    IconComponent,
    DropdownMenuComponent,
    DeploymentStatusStripComponent,
    ReadinessScorecardComponent,
    EnvironmentDriftBannerComponent,
    ProjectHealthBannerComponent,
    StatusBadgeComponent
  ],
  templateUrl: './project-detail.component.html',
  styleUrl: './project-detail.component.scss'
})
export class ProjectDetailComponent implements OnInit {
  readonly project = signal<ProjectDetail | null>(null);
  readonly services = signal<ProjectServicesResponse | null>(null);
  readonly latestDeployment = signal<DeploymentSummary | null>(null);
  readonly envVars = signal<Record<string, ProviderEnvVar[]>>({});
  readonly loadingEnv = signal<Record<string, boolean>>({});
  readonly savingEnv = signal<Record<string, boolean>>({});
  readonly serviceStatuses = signal<Record<string, string>>({});
  readonly serviceStatusDetails = signal<Record<string, ProjectServiceStatus>>({});
  readonly copiedUrl = signal<string | null>(null);
  readonly loadingStatus = signal<Record<string, boolean>>({});
  readonly actingOnService = signal<Record<string, boolean>>({});
  readonly provisioning = signal(false);
  readonly detectingRequirements = signal(false);
  readonly databaseRequirements = signal<DatabaseRequirementProfile | null>(null);
  readonly expandedEnv = signal<Record<string, boolean>>({});
  readonly dataServiceInfo = signal<Record<string, DataServiceInfo | null>>({});
  readonly loadingDataInfo = signal<Record<string, boolean>>({});
  readonly expandedDataInfo = signal<Record<string, boolean>>({});
  readonly deleting = signal(false);
  readonly deploymentReadiness = signal<DeploymentReadinessResult | null>(null);
  readonly loadingDeploymentReadiness = signal(false);

  setupPostgres = true;
  setupRedis = true;

  newEnvKey: Record<string, string> = {};
  newEnvValue: Record<string, string> = {};

  // The DeployAI-managed environment variables (the set entered in the wizard), editable here.
  readonly managedEnv = signal<EnvVariable[]>([]);
  readonly loadingManagedEnv = signal(false);
  readonly savingManagedEnvKey = signal<string | null>(null);
  readonly revealedEnv = signal<Record<string, boolean>>({});
  readonly unreadableEnvTargets = signal<UnreadableEnvTarget[]>([]);
  // Working copy of each var's value, so edits stay local until saved.
  managedEnvDraft: Record<string, string> = {};
  newManagedKey = '';
  newManagedValue = '';
  newManagedSecret = false;
  // Which app receives the variable. Without this the API picks, and it picks the website — so on a
  // split deploy a server-side setting lands in the frontend container, where nothing reads it and
  // nothing says so. A CORS origin set this way left the API rejecting the site's own requests.
  newManagedTargetId = '';

  // Configuration the running app said it lacked. Distinct from managedEnv, which is only what
  // DeployAI already knows about — the whole point is the keys nobody knew to ask for.
  readonly missingConfig = signal<MissingConfigurationItem[]>([]);
  readonly missingConfigService = signal<ProjectServiceView | null>(null);
  readonly missingConfigUnreadable = signal<string | null>(null);
  readonly checkingMissingConfig = signal(false);

  projectId = '';

  private copiedTimer?: ReturnType<typeof setTimeout>;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService,
    private readonly toast: ToastService,
    private readonly confirm: ConfirmService,
    readonly aiSetup: AiSetupPreferenceService,
    readonly projectsStore: ProjectsStore
  ) {}

  ngOnInit(): void {
    this.projectId = this.route.snapshot.paramMap.get('id') ?? '';
    if (this.projectId) {
      this.aiSetup.hydrateForProject(this.projectId);
    }
    this.loadProject();
  }

  databaseEngineLabel = databaseEngineLabel;
  providerLabel = providerLabel;
  serviceStatusLabel = serviceStatusLabel;

  overallStatus(): string {
    return this.latestDeployment()?.status ?? 'idle';
  }

  hasDeployed(): boolean {
    return this.latestDeployment() !== null;
  }

  lastDeployedLabel(): string | null {
    const deployment = this.latestDeployment();
    const when = deployment?.completedAt ?? deployment?.startedAt;
    if (!when) {
      return null;
    }
    return this.relativeTime(when);
  }

  liveUrls(): LiveUrl[] {
    const urls: LiveUrl[] = [];
    const sync = this.project()?.environmentSync;
    const deployment = this.latestDeployment();

    // Role first: matching on provider name found only the first of two Coolify targets, so a
    // full-stack app on one server surfaced one URL and labelled it "Coolify app".
    const byRole = (role: string) =>
      deployment?.targets.find(t => t.role?.toLowerCase() === role)?.deployUrl;

    const websiteUrl =
      sync?.resolvedWebsiteUrl ||
      byRole('website') ||
      deployment?.targets.find(t => t.providerName === ProviderName.Vercel)?.deployUrl ||
      this.serviceDeployUrl(ProviderName.Vercel);
    if (websiteUrl) {
      urls.push({ label: 'Website', url: websiteUrl });
    }

    const apiUrl =
      sync?.resolvedApiUrl ||
      byRole('server') ||
      deployment?.targets.find(t => t.providerName === ProviderName.Railway)?.deployUrl ||
      this.serviceDeployUrl(ProviderName.Railway);
    // Under a single-origin deployment the API has no URL of its own — it is reached through
    // the website — so an apiUrl equal to the website URL is noise, not a second link.
    if (apiUrl && apiUrl !== websiteUrl) {
      urls.push({ label: 'API', url: apiUrl });
    }

    // Only when neither role resolved: an older target, or a publish-only Coolify app.
    if (urls.length === 0) {
      const coolifyUrl =
        deployment?.targets.find(t => t.providerName === ProviderName.Coolify)?.deployUrl ||
        this.serviceDeployUrl(ProviderName.Coolify);
      if (coolifyUrl) {
        urls.push({ label: 'Live site', url: coolifyUrl });
      }
    }

    return urls;
  }

  coolifyBranchMismatch(): string | null {
    const project = this.project();
    if (!project) {
      return null;
    }

    for (const target of project.targets) {
      if (target.providerName !== ProviderName.Coolify) {
        continue;
      }

      const config = parseTargetConfig(target.config);
      const coolifyBranch = config.coolifyGitBranch;
      if (coolifyBranch && coolifyBranch !== project.defaultBranch) {
        return `Coolify rebuilds from branch "${coolifyBranch}", but this app uses "${project.defaultBranch}".`;
      }
    }

    return null;
  }

  serviceOpenUrl(service: ProjectServiceView): string | undefined {
    return this.serviceStatusDetails()[service.id]?.deployUrl;
  }

  displayUrl(url: string): string {
    return url.replace(/^https?:\/\//i, '').replace(/\/$/, '');
  }

  async copyUrl(url: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(url);
      this.copiedUrl.set(url);
      clearTimeout(this.copiedTimer);
      this.copiedTimer = setTimeout(() => this.copiedUrl.set(null), 2000);
      this.toast.success('Link copied');
    } catch {
      this.toast.error('Could not copy link');
    }
  }

  goToProjects(): void {
    void this.router.navigate(['/dashboard']);
  }

  private serviceDeployUrl(providerName: string): string | undefined {
    const service = (this.services()?.applicationServices ?? []).find(
      s => s.providerName === providerName
    );
    if (!service) {
      return undefined;
    }
    return this.serviceStatusDetails()[service.id]?.deployUrl;
  }

  private relativeTime(value: string): string {
    const then = new Date(value).getTime();
    const diffMs = Date.now() - then;
    const seconds = Math.round(diffMs / 1000);
    if (seconds < 60) {
      return 'just now';
    }
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) {
      return `${minutes}m ago`;
    }
    const hours = Math.round(minutes / 60);
    if (hours < 24) {
      return `${hours}h ago`;
    }
    const days = Math.round(hours / 24);
    if (days < 30) {
      return `${days}d ago`;
    }
    return new Date(value).toLocaleDateString();
  }

  openTroubleshoot(): void {
    void this.router.navigate(['/projects', this.projectId, 'troubleshoot']);
  }

  databaseIcon(engine?: string): IconName {
    if (engine?.toLowerCase().includes('redis')) {
      return 'settings';
    }
    return 'folder';
  }

  hasPostgresService(): boolean {
    return (this.services()?.dataServices ?? []).some(
      service => service.databaseEngine === 'postgres'
    );
  }

  hasRedisService(): boolean {
    return (this.services()?.dataServices ?? []).some(
      service => service.databaseEngine === 'redis'
    );
  }

  envVarsFor(targetId: string): ProviderEnvVar[] {
    return this.envVars()[targetId] ?? [];
  }

  isEnvExpanded(targetId: string): boolean {
    return this.expandedEnv()[targetId] ?? false;
  }

  toggleEnvPanel(service: ProjectServiceView): void {
    const targetId = service.id;
    const nextExpanded = !this.isEnvExpanded(targetId);
    this.expandedEnv.update(state => ({ ...state, [targetId]: nextExpanded }));
    if (nextExpanded && !this.envVars()[targetId]) {
      this.loadEnvVars(service);
    }
  }

  loadEnvVars(service: ProjectServiceView): void {
    const targetId = service.id;
    this.loadingEnv.update(state => ({ ...state, [targetId]: true }));
    this.api.listProjectEnvVars(service.credentialId, service.providerProjectId).subscribe({
      next: (response) => {
        this.envVars.update(state => ({ ...state, [targetId]: response.envVars }));
        this.loadingEnv.update(state => ({ ...state, [targetId]: false }));
      },
      error: () => {
        this.loadingEnv.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  addEnv(service: ProjectServiceView): void {
    const targetId = service.id;
    const key = this.newEnvKey[targetId]?.trim();
    const value = this.newEnvValue[targetId];
    if (!key || !value) {
      return;
    }

    this.savingEnv.update(state => ({ ...state, [targetId]: true }));
    this.api.upsertProjectEnvVar(service.credentialId, service.providerProjectId, {
      key,
      value,
      type: 'encrypted'
    }).subscribe({
      next: () => {
        this.newEnvKey[targetId] = '';
        this.newEnvValue[targetId] = '';
        this.savingEnv.update(state => ({ ...state, [targetId]: false }));
        this.loadEnvVars(service);
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not save that setting.');
        this.savingEnv.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  removeEnv(service: ProjectServiceView, envVarId: string): void {
    this.api.deleteProjectEnvVar(service.credentialId, service.providerProjectId, envVarId).subscribe({
      next: () => this.loadEnvVars(service),
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not remove that setting.')
    });
  }

  loadServiceStatus(service: ProjectServiceView): void {
    if (!service.canManage) {
      return;
    }

    const targetId = service.id;
    this.loadingStatus.update(state => ({ ...state, [targetId]: true }));
    this.api.getProjectServiceStatus(this.projectId, targetId).subscribe({
      next: (response) => {
        this.serviceStatuses.update(state => ({ ...state, [targetId]: response.status }));
        this.serviceStatusDetails.update(state => ({ ...state, [targetId]: response }));
        this.loadingStatus.update(state => ({ ...state, [targetId]: false }));
      },
      error: () => {
        this.serviceStatuses.update(state => ({ ...state, [targetId]: 'unknown' }));
        this.loadingStatus.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  redeployService(service: ProjectServiceView): void {
    const targetId = service.id;
    this.actingOnService.update(state => ({ ...state, [targetId]: true }));
    this.api.redeployProjectService(this.projectId, targetId).subscribe({
      next: () => {
        this.toast.success('Service restart requested.');
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
        this.loadServiceStatus(service);
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not restart that service.');
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  async removeService(service: ProjectServiceView): Promise<void> {
    const label = databaseEngineLabel(service.databaseEngine);
    const confirmed = await this.confirm.ask({
      title: `Remove ${label}?`,
      message: `This deletes the ${label} service from Railway and unlinks it from your app. This cannot be undone.`,
      confirmLabel: 'Remove',
      destructive: true
    });
    if (!confirmed) {
      return;
    }

    const targetId = service.id;
    this.actingOnService.update(state => ({ ...state, [targetId]: true }));
    this.api.removeProjectService(this.projectId, targetId).subscribe({
      next: (response) => {
        this.services.set(response);
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
        this.toast.success(`${label} removed.`);
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not remove that service.');
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  autoSetupDatabases(): void {
    this.provisioning.set(true);
    this.api.autoProvisionRailwayDatabases(this.projectId).subscribe({
      next: () => this.refreshAfterProvisioning(),
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not set up databases from your repo.');
        this.provisioning.set(false);
      }
    });
  }

  addSelectedDatabases(): void {
    if (!this.setupPostgres && !this.setupRedis) {
      this.toast.error('Choose at least one database to add.');
      return;
    }

    this.provisioning.set(true);
    this.api.provisionRailwayDatabases(this.projectId, {
      postgres: this.setupPostgres,
      redis: this.setupRedis
    }).subscribe({
      next: () => this.refreshAfterProvisioning(),
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not add those databases.');
        this.provisioning.set(false);
      }
    });
  }

  publish(): void {
    const readiness = this.deploymentReadiness();
    if (this.aiSetup.enabled() && readiness?.usesSplitOrigin && !readiness.isReady) {
      this.toast.error('Generate split-origin deployment files before publishing.');
      return;
    }

    this.projectsStore.triggerDeploy(this.projectId);
  }

  openHistory(): void {
    void this.router.navigate(['/projects', this.projectId, 'history']);
  }

  openEdit(): void {
    void this.router.navigate(['/projects', this.projectId, 'edit']);
  }

  serviceFolderSummary(service: ProjectServiceView): string | null {
    const folder = service.serviceDirectory ?? service.rootDirectory;
    return folder ? `Folder: ${folder}` : null;
  }

  linkedConnectionSummary(service: ProjectServiceView): string | null {
    if (!service.linkedConnectionKeys.length) {
      return null;
    }

    return `Linked to app as ${service.linkedConnectionKeys.join(', ')}`;
  }

  dataInfoFor(serviceId: string): DataServiceInfo | null {
    return this.dataServiceInfo()[serviceId] ?? null;
  }

  isDataInfoExpanded(serviceId: string): boolean {
    return this.expandedDataInfo()[serviceId] ?? false;
  }

  toggleDataInfoPanel(service: ProjectServiceView): void {
    const targetId = service.id;
    const nextExpanded = !this.isDataInfoExpanded(targetId);
    this.expandedDataInfo.update(state => ({ ...state, [targetId]: nextExpanded }));
    if (nextExpanded && !this.dataInfoFor(targetId)) {
      this.loadDataServiceInfo(service);
    }
  }

  refreshDataServiceInfo(service: ProjectServiceView): void {
    this.loadDataServiceInfo(service);
  }

  loadDataServiceInfo(service: ProjectServiceView): void {
    const targetId = service.id;
    this.loadingDataInfo.update(state => ({ ...state, [targetId]: true }));
    this.api.getDataServiceInfo(this.projectId, targetId).subscribe({
      next: (info) => {
        this.dataServiceInfo.update(state => ({ ...state, [targetId]: info }));
        this.loadingDataInfo.update(state => ({ ...state, [targetId]: false }));
      },
      error: (err) => {
        this.dataServiceInfo.update(state => ({ ...state, [targetId]: null }));
        this.loadingDataInfo.update(state => ({ ...state, [targetId]: false }));
        this.toast.error(err?.error?.error?.message ?? 'Could not load database details.');
      }
    });
  }

  dataServiceSummary(service: ProjectServiceView): string | null {
    const info = this.dataInfoFor(service.id);
    if (!info) {
      return null;
    }

    const parts: string[] = [];
    if (info.metadata.databaseName) {
      parts.push(`Database: ${info.metadata.databaseName}`);
    }
    if (info.metadata.volumeMountPath) {
      parts.push(`Volume: ${info.metadata.volumeMountPath}`);
    }
    if (info.connectionSummary) {
      parts.push(info.connectionSummary);
    }

    return parts.length ? parts.join(' · ') : null;
  }

  tableCount(serviceId: string): number {
    return this.dataInfoFor(serviceId)?.inspection?.tables.length ?? 0;
  }

  connectionStatusLabel(serviceId: string): string | null {
    const inspection = this.dataInfoFor(serviceId)?.inspection;
    if (!inspection) {
      return null;
    }

    return inspection.connected ? 'OK' : 'Unreachable';
  }

  async removeApp(): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: 'Remove this app?',
      message: `This removes ${this.project()?.name ?? 'this app'} and unlinks its connected services. This cannot be undone.`,
      confirmLabel: 'Remove app',
      cancelLabel: 'Keep app',
      destructive: true
    });
    if (!confirmed) {
      return;
    }

    this.deleting.set(true);
    this.api.deleteProject(this.projectId).subscribe({
      next: () => void this.router.navigate(['/dashboard']),
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not remove that app.');
        this.deleting.set(false);
      }
    });
  }

  applicationServiceMenuItems(service: ProjectServiceView): DropdownMenuItem[] {
    const acting = this.actingOnService()[service.id];
    const items: DropdownMenuItem[] = [];
    const openUrl = this.serviceOpenUrl(service);

    if (openUrl) {
      items.push({ id: 'open', label: 'Open', icon: 'external', href: openUrl });
    }

    if (service.canManage) {
      items.push({ id: 'restart', label: 'Restart', icon: 'refresh', disabled: acting, loading: acting });
    }

    items.push({
      id: 'env',
      label: this.isEnvExpanded(service.id) ? 'Hide env' : 'Env vars',
      icon: 'settings'
    });

    return items;
  }

  onApplicationServiceMenuAction(service: ProjectServiceView, action: string): void {
    switch (action) {
      case 'restart':
        this.redeployService(service);
        break;
      case 'env':
        this.toggleEnvPanel(service);
        break;
      default:
        break;
    }
  }

  dataServiceMenuItems(service: ProjectServiceView): DropdownMenuItem[] {
    const acting = this.actingOnService()[service.id];
    const items: DropdownMenuItem[] = [];
    const railwayUrl = this.dataInfoFor(service.id)?.railwayUrl;

    if (railwayUrl) {
      items.push({ id: 'railway', label: 'Railway', icon: 'external', href: railwayUrl });
    }

    items.push(
      { id: 'restart', label: 'Restart', icon: 'refresh', disabled: acting, loading: acting },
      { id: 'remove', label: 'Remove', icon: 'trash', destructive: true, disabled: acting },
      {
        id: 'db-info',
        label: this.isDataInfoExpanded(service.id) ? 'Hide DB' : 'DB info',
        icon: 'info'
      },
      {
        id: 'env',
        label: this.isEnvExpanded(service.id) ? 'Hide env' : 'Env vars',
        icon: 'settings'
      }
    );

    return items;
  }

  onDataServiceMenuAction(service: ProjectServiceView, action: string): void {
    switch (action) {
      case 'restart':
        this.redeployService(service);
        break;
      case 'remove':
        this.removeService(service);
        break;
      case 'db-info':
        this.toggleDataInfoPanel(service);
        break;
      case 'env':
        this.toggleEnvPanel(service);
        break;
      default:
        break;
    }
  }

  hasSplitOrigin(): boolean {
    return this.deploymentReadiness()?.usesSplitOrigin ?? false;
  }

  private loadProject(): void {
    this.api.getProject(this.projectId).subscribe({
      next: (project) => {
        this.project.set(project);
        this.loadServices();
        this.loadManagedEnv();
        if (this.aiSetup.enabled()) {
          this.loadDeploymentReadiness();
        }
        this.loadLatestDeployment();
      },
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not load that app.')
    });
  }

  private loadManagedEnv(): void {
    this.loadingManagedEnv.set(true);
    this.api.getEnvironment(this.projectId).subscribe({
      next: (response) => {
        // Server order already groups by app; re-sorting by key alone would interleave them.
        const vars = response.variables;
        this.managedEnv.set(vars);
        this.unreadableEnvTargets.set(response.unreadable ?? []);
        // Keyed by app as well as name: the same key can legitimately exist on both containers
        // with different values, and a single map would let one overwrite the other's draft.
        this.managedEnvDraft = Object.fromEntries(vars.map(v => [this.envRowId(v), v.value]));
        this.revealedEnv.set({});
        this.loadingManagedEnv.set(false);
      },
      error: () => {
        this.managedEnv.set([]);
        this.unreadableEnvTargets.set([]);
        this.loadingManagedEnv.set(false);
      }
    });
  }

  /**
   * Asks the running app what it lacks. Repository scanning reads a fixed set of files at a repo's
   * root, so an app whose configuration lives deeper produces nothing to ask for — and then says
   * exactly what it needed in its own startup output. This reads that.
   */
  checkMissingConfig(): void {
    // The server if there is one — it is where startup configuration failures show up — otherwise
    // the first application. Data services have no application logs to read.
    const apps = this.services()?.applicationServices ?? [];
    const service = apps.find(s => s.role === 'server') ?? apps[0] ?? null;
    if (!service) {
      return;
    }

    this.checkingMissingConfig.set(true);
    this.missingConfigService.set(service);
    this.api.getMissingConfiguration(this.projectId, service.id).subscribe({
      next: (response) => {
        this.checkingMissingConfig.set(false);
        this.missingConfig.set(response.missing ?? []);
        // "Could not look" is not "nothing missing". A stopped container is the usual cause and is
        // precisely the state worth reporting, so it gets its own message rather than an empty list.
        this.missingConfigUnreadable.set(
          response.readable ? null : (response.message ?? "Could not read this app's logs.")
        );
      },
      error: (err) => {
        this.checkingMissingConfig.set(false);
        this.missingConfig.set([]);
        this.missingConfigUnreadable.set(
          err?.error?.error?.message ?? "Could not read this app's logs."
        );
      }
    });
  }

  /**
   * Puts a finding into the add-variable fields for review rather than saving it. A section gives a
   * prefix to complete — .NET says "Jwt configuration missing" without saying whether it wants
   * Jwt__Key or Jwt__Issuer — so the key is left unfinished on purpose.
   */
  useMissingConfig(item: MissingConfigurationItem): void {
    this.newManagedKey = item.kind === 'section' ? `${item.name}__` : item.name;
    this.newManagedValue = item.suggestedValue;
    this.newManagedSecret = true;
    this.toast.success(
      item.kind === 'section'
        ? `Complete the key below — the app reported the "${item.name}" section, not a single variable.`
        : `${item.name} filled in below. Review it, then add.`
    );
  }

  isEnvRevealed(key: string): boolean {
    return this.revealedEnv()[key] ?? false;
  }

  toggleEnvReveal(key: string): void {
    this.revealedEnv.update(state => ({ ...state, [key]: !state[key] }));
  }

  /** Identifies a row by app as well as name — the same key can exist on both containers. */
  envRowId(item: EnvVariable): string {
    return `${item.targetId ?? 'unplaced'}:${item.key}`;
  }

  /** The variables on one app, in the order the server grouped them. */
  envGroups(): { targetId: string | null; role: string; items: EnvVariable[] }[] {
    const groups = new Map<string, { targetId: string | null; role: string; items: EnvVariable[] }>();
    for (const item of this.managedEnv()) {
      const id = item.targetId ?? 'unplaced';
      if (!groups.has(id)) {
        groups.set(id, {
          targetId: item.targetId,
          // A variable DeployAI stores that no app reported back: either its app could not be read,
          // or the value never reached the provider. Both are worth showing, not hiding.
          role: item.targetRole ?? 'not on any app',
          items: []
        });
      }
      groups.get(id)!.items.push(item);
    }
    return [...groups.values()];
  }

  envValueChanged(item: EnvVariable): boolean {
    return (this.managedEnvDraft[this.envRowId(item)] ?? '') !== item.value;
  }

  saveManagedEnv(item: EnvVariable): void {
    const value = this.managedEnvDraft[this.envRowId(item)] ?? '';
    if (!value || value === item.value) {
      return;
    }

    this.savingManagedEnvKey.set(this.envRowId(item));
    this.api.setComposeEnvironment(
      this.projectId,
      [{ key: item.key, value, isSecret: item.isSecret }],
      // Save back to the app it came from. Without this it would go to the API's default (the
      // website), so editing a server variable would write a copy to the frontend instead.
      item.targetId ?? undefined
    ).subscribe({
      next: () => {
        this.savingManagedEnvKey.set(null);
        this.toast.success(`${item.key} saved. Redeploy to apply it.`);
        this.loadManagedEnv();
      },
      error: (err) => {
        this.savingManagedEnvKey.set(null);
        this.toast.error(err?.error?.error?.message ?? 'Could not save that variable.');
      }
    });
  }

  /**
   * The apps a variable can be sent to. A project with one app needs no choice; a split deploy does,
   * because the two containers read entirely different configuration.
   */
  envTargets(): ProjectServiceView[] {
    return this.services()?.applicationServices ?? [];
  }

  /** Defaults to the server: hand-typed configuration is server-side far more often than not. */
  resolveManagedTargetId(): string {
    if (this.newManagedTargetId) {
      return this.newManagedTargetId;
    }

    const apps = this.envTargets();
    return (apps.find(s => s.role === 'server') ?? apps[0])?.id ?? '';
  }

  addManagedEnv(): void {
    const key = this.newManagedKey.trim();
    const value = this.newManagedValue;
    if (!key || !value) {
      return;
    }

    const targetId = this.resolveManagedTargetId();
    const targetName = this.envTargets().find(s => s.id === targetId)?.role ?? 'app';

    this.savingManagedEnvKey.set(key);
    this.api.setComposeEnvironment(
      this.projectId,
      [{ key, value, isSecret: this.newManagedSecret }],
      targetId || undefined
    ).subscribe({
      next: () => {
        this.savingManagedEnvKey.set(null);
        this.newManagedKey = '';
        this.newManagedValue = '';
        this.newManagedSecret = false;
        this.toast.success(`${key} added to the ${targetName}. Redeploy to apply it.`);
        this.loadManagedEnv();
      },
      error: (err) => {
        this.savingManagedEnvKey.set(null);
        this.toast.error(err?.error?.error?.message ?? 'Could not add that variable.');
      }
    });
  }

  async deleteManagedEnv(item: EnvVariable): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Remove ${item.key}?`,
      message: item.targetRole
        ? `This removes ${item.key} from the ${item.targetRole}. Redeploy to apply.`
        : 'This removes the variable from the live app and from DeployAI. Redeploy to apply.',
      confirmLabel: 'Remove',
      destructive: true
    });
    if (!confirmed) {
      return;
    }

    this.savingManagedEnvKey.set(this.envRowId(item));
    // From the app it is actually on. Defaulting would delete DeployAI's record while leaving the
    // value live on the other container.
    this.api.deleteEnvironmentVariable(this.projectId, item.key, item.targetId ?? undefined).subscribe({
      next: () => {
        this.savingManagedEnvKey.set(null);
        this.toast.success(`${item.key} removed.`);
        this.loadManagedEnv();
      },
      error: (err) => {
        this.savingManagedEnvKey.set(null);
        this.toast.error(err?.error?.error?.message ?? 'Could not remove that variable.');
      }
    });
  }

  private loadLatestDeployment(): void {
    this.api.listDeployments(this.projectId).subscribe({
      next: (response) => this.latestDeployment.set(response.deployments[0] ?? null),
      error: () => this.latestDeployment.set(null)
    });
  }

  private loadDeploymentReadiness(): void {
    const project = this.project();
    if (!project) {
      return;
    }

    this.loadingDeploymentReadiness.set(true);
    this.api.getProjectDeploymentReadiness(this.projectId, project.defaultBranch).subscribe({
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

  private loadServices(): void {
    this.api.getProjectServices(this.projectId).subscribe({
      next: (response) => {
        this.services.set(response);
        // Bind the picker once the apps are known, so it opens on the server rather than blank.
        this.newManagedTargetId = this.resolveManagedTargetId();
        for (const service of [...response.applicationServices, ...response.dataServices]) {
          this.newEnvKey[service.id] = '';
          this.newEnvValue[service.id] = '';
          if (service.canManage) {
            this.loadServiceStatus(service);
          }
          if (service.databaseEngine) {
            this.loadDataServiceInfo(service);
          }
        }

        if (response.hasManagedServer) {
          this.loadDatabaseRequirements();
        }
      },
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not load services.')
    });
  }

  private loadDatabaseRequirements(): void {
    const project = this.project();
    if (!project) {
      return;
    }

    const [owner, repo] = project.githubRepoFullName.split('/');
    const server = (this.services()?.applicationServices ?? []).find(
      service => service.providerName === 'railway'
    );
    const path = server?.serviceDirectory ?? server?.rootDirectory ?? '';

    if (!owner || !repo) {
      return;
    }

    this.detectingRequirements.set(true);
    this.api.detectDatabaseRequirements(owner, repo, path, project.defaultBranch).subscribe({
      next: (profile) => {
        this.databaseRequirements.set(profile);
        this.setupPostgres = profile.requiresPostgres;
        this.setupRedis = profile.requiresRedis;
        this.detectingRequirements.set(false);
      },
      error: () => {
        this.databaseRequirements.set(null);
        this.detectingRequirements.set(false);
      }
    });
  }

  private refreshAfterProvisioning(): void {
    this.provisioning.set(false);
    this.api.getProjectServices(this.projectId).subscribe({
      next: (response) => {
        this.services.set(response);
        for (const service of response.dataServices) {
          if (service.canManage) {
            this.loadServiceStatus(service);
          }
          this.loadDataServiceInfo(service);
        }
      }
    });
  }
}
