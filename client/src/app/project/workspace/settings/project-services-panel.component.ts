import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../../../core/services/api.service';
import {
  DatabaseRequirementProfile,
  DataServiceInfo,
  EnvVariable,
  MissingConfigurationItem,
  ProjectServiceStatus,
  ProjectServiceView,
  ProjectServicesResponse,
  ProviderEnvVar,
  UnreadableEnvTarget
} from '../../../core/models/api.models';
import { databaseEngineLabel, providerLabel, serviceStatusLabel } from '../../../core/utils/target-config';
import { ButtonComponent } from '../../../shared/ui/button/button.component';
import { IconComponent, IconName } from '../../../shared/ui/icon/icon.component';
import { DropdownMenuComponent, DropdownMenuItem } from '../../../shared/ui/dropdown/dropdown-menu.component';
import { StatusBadgeComponent } from '../../../shared/status-badge/status-badge.component';
import { ConfirmService } from '../../../shared/ui/confirm/confirm.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';

@Component({
  selector: 'app-project-services-panel',
  standalone: true,
  imports: [FormsModule, ButtonComponent, IconComponent, DropdownMenuComponent, StatusBadgeComponent],
  templateUrl: './project-services-panel.component.html',
  styleUrl: './project-services-panel.component.scss'
})
export class ProjectServicesPanelComponent implements OnInit {
  readonly services = signal<ProjectServicesResponse | null>(null);
  readonly envVars = signal<Record<string, ProviderEnvVar[]>>({});
  readonly loadingEnv = signal<Record<string, boolean>>({});
  readonly savingEnv = signal<Record<string, boolean>>({});
  readonly serviceStatuses = signal<Record<string, string>>({});
  readonly serviceStatusDetails = signal<Record<string, ProjectServiceStatus>>({});
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
  managedEnvDraft: Record<string, string> = {};
  newManagedKey = '';
  newManagedValue = '';
  newManagedSecret = false;
  newManagedTargetId = '';

  readonly missingConfig = signal<MissingConfigurationItem[]>([]);
  readonly missingConfigService = signal<ProjectServiceView | null>(null);
  readonly missingConfigUnreadable = signal<string | null>(null);
  readonly checkingMissingConfig = signal(false);

  projectId = '';
  private projectName = '';
  private projectGithubRepoFullName = '';
  private projectDefaultBranch = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService,
    private readonly toast: ToastService,
    private readonly confirm: ConfirmService
  ) {}

  ngOnInit(): void {
    this.projectId = this.route.snapshot.paramMap.get('id') ?? '';
    this.loadServices();
    this.loadManagedEnv();
  }

  databaseEngineLabel = databaseEngineLabel;
  providerLabel = providerLabel;
  serviceStatusLabel = serviceStatusLabel;

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
      message: `This removes ${this.projectName || 'this app'} and unlinks its connected services. This cannot be undone.`,
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
    const openUrl = this.serviceStatusDetails()[service.id]?.deployUrl;

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

  /**
   * Asks the running app what it lacks. Repository scanning reads a fixed set of files at a repo's
   * root, so an app whose configuration lives deeper produces nothing to ask for — and then says
   * exactly what it needed in its own startup output. This reads that.
   */
  checkMissingConfig(): void {
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

  envRowId(item: EnvVariable): string {
    return `${item.targetId ?? 'unplaced'}:${item.key}`;
  }

  envGroups(): { targetId: string | null; role: string; items: EnvVariable[] }[] {
    const groups = new Map<string, { targetId: string | null; role: string; items: EnvVariable[] }>();
    for (const item of this.managedEnv()) {
      const id = item.targetId ?? 'unplaced';
      if (!groups.has(id)) {
        groups.set(id, {
          targetId: item.targetId,
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

  envTargets(): ProjectServiceView[] {
    return this.services()?.applicationServices ?? [];
  }

  resolveManagedTargetId(): string {
    if (this.newManagedTargetId) {
      return this.newManagedTargetId;
    }

    const apps = this.envTargets();
    return (apps.find(s => s.role === 'server') ?? apps[0])?.id ?? '';
  }

  downloadEnvironment(): void {
    this.api.exportEnvironment(this.projectId).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `${this.projectName || 'settings'}.env`;
        link.click();
        URL.revokeObjectURL(url);
      },
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not export the settings.')
    });
  }

  generateManagedValue(): void {
    const key = this.newManagedKey.trim();
    if (!key) {
      return;
    }

    this.api.generateEnvValue(this.projectId, key).subscribe({
      next: ({ value }) => {
        this.newManagedValue = value;
        this.newManagedSecret = true;
      },
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not generate a value.')
    });
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

  private loadManagedEnv(): void {
    this.loadingManagedEnv.set(true);
    this.api.getEnvironment(this.projectId).subscribe({
      next: (response) => {
        const vars = response.variables;
        this.managedEnv.set(vars);
        this.unreadableEnvTargets.set(response.unreadable ?? []);
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

  private loadServices(): void {
    this.api.getProject(this.projectId).subscribe({
      next: (project) => {
        this.projectName = project.name;
        this.projectGithubRepoFullName = project.githubRepoFullName;
        this.projectDefaultBranch = project.defaultBranch;
      }
    });

    this.api.getProjectServices(this.projectId).subscribe({
      next: (response) => {
        this.services.set(response);
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
    const server = (this.services()?.applicationServices ?? []).find(
      service => service.providerName === 'railway'
    );
    const path = server?.serviceDirectory ?? server?.rootDirectory ?? '';
    const [owner, repo] = this.projectGithubRepoFullName.split('/');
    if (!owner || !repo) {
      return;
    }

    this.detectingRequirements.set(true);
    this.api.detectDatabaseRequirements(owner, repo, path, this.projectDefaultBranch).subscribe({
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
