import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import {
  DatabaseRequirementProfile,
  ProjectDetail,
  ProjectServiceView,
  ProjectServicesResponse,
  ProviderEnvVar
} from '../core/models/api.models';
import {
  databaseEngineLabel,
  parseTargetConfig,
  providerLabel,
  serviceStatusLabel
} from '../core/utils/target-config';

@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './project-detail.component.html',
  styleUrl: './project-detail.component.scss'
})
export class ProjectDetailComponent implements OnInit {
  readonly project = signal<ProjectDetail | null>(null);
  readonly services = signal<ProjectServicesResponse | null>(null);
  readonly publishing = signal(false);
  readonly message = signal<string | null>(null);
  readonly envVars = signal<Record<string, ProviderEnvVar[]>>({});
  readonly loadingEnv = signal<Record<string, boolean>>({});
  readonly savingEnv = signal<Record<string, boolean>>({});
  readonly serviceStatuses = signal<Record<string, string>>({});
  readonly loadingStatus = signal<Record<string, boolean>>({});
  readonly actingOnService = signal<Record<string, boolean>>({});
  readonly provisioning = signal(false);
  readonly detectingRequirements = signal(false);
  readonly databaseRequirements = signal<DatabaseRequirementProfile | null>(null);
  readonly expandedEnv = signal<Record<string, boolean>>({});

  setupPostgres = true;
  setupRedis = true;

  newEnvKey: Record<string, string> = {};
  newEnvValue: Record<string, string> = {};

  private projectId = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService
  ) {}

  ngOnInit(): void {
    this.projectId = this.route.snapshot.paramMap.get('id') ?? '';
    this.loadProject();
  }

  databaseEngineLabel = databaseEngineLabel;
  providerLabel = providerLabel;
  serviceStatusLabel = serviceStatusLabel;

  statusClass(serviceId: string): string {
    return this.serviceStatuses()[serviceId] || 'unknown';
  }

  targetSummary(): string {
    const project = this.project();
    if (!project) {
      return '';
    }

    const labels = project.targets
      .filter(target => parseTargetConfig(target.config).role !== 'database')
      .map(target => providerLabel(target.providerName));
    return labels.length ? `publishes to ${labels.join(' + ')}` : 'no destinations yet';
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
        this.message.set(err?.error?.error?.message ?? 'Could not save that setting.');
        this.savingEnv.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  removeEnv(service: ProjectServiceView, envVarId: string): void {
    this.api.deleteProjectEnvVar(service.credentialId, service.providerProjectId, envVarId).subscribe({
      next: () => this.loadEnvVars(service),
      error: (err) => this.message.set(err?.error?.error?.message ?? 'Could not remove that setting.')
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
        this.message.set('Service restart requested.');
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
        this.loadServiceStatus(service);
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not restart that service.');
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  removeService(service: ProjectServiceView): void {
    const label = databaseEngineLabel(service.databaseEngine);
    if (!confirm(`Remove ${label} from Railway? This deletes the service and unlinks it from your app.`)) {
      return;
    }

    const targetId = service.id;
    this.actingOnService.update(state => ({ ...state, [targetId]: true }));
    this.api.removeProjectService(this.projectId, targetId).subscribe({
      next: (response) => {
        this.services.set(response);
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
        this.message.set(`${label} removed.`);
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not remove that service.');
        this.actingOnService.update(state => ({ ...state, [targetId]: false }));
      }
    });
  }

  autoSetupDatabases(): void {
    this.provisioning.set(true);
    this.message.set(null);
    this.api.autoProvisionRailwayDatabases(this.projectId).subscribe({
      next: () => this.refreshAfterProvisioning(),
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not set up databases from your repo.');
        this.provisioning.set(false);
      }
    });
  }

  addSelectedDatabases(): void {
    if (!this.setupPostgres && !this.setupRedis) {
      this.message.set('Choose at least one database to add.');
      return;
    }

    this.provisioning.set(true);
    this.message.set(null);
    this.api.provisionRailwayDatabases(this.projectId, {
      postgres: this.setupPostgres,
      redis: this.setupRedis
    }).subscribe({
      next: () => this.refreshAfterProvisioning(),
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not add those databases.');
        this.provisioning.set(false);
      }
    });
  }

  publish(): void {
    this.publishing.set(true);
    this.api.triggerDeployment(this.projectId).subscribe({
      next: (response) => void this.router.navigate(['/publish', response.deploymentId]),
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Publishing did not start.');
        this.publishing.set(false);
      }
    });
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

    return 'Linked to app as database connection';
  }

  private loadProject(): void {
    this.api.getProject(this.projectId).subscribe({
      next: (project) => {
        this.project.set(project);
        this.loadServices();
      },
      error: (err) => this.message.set(err?.error?.error?.message ?? 'Could not load that app.')
    });
  }

  private loadServices(): void {
    this.api.getProjectServices(this.projectId).subscribe({
      next: (response) => {
        this.services.set(response);
        for (const service of [...response.applicationServices, ...response.dataServices]) {
          this.newEnvKey[service.id] = '';
          this.newEnvValue[service.id] = '';
          if (service.canManage) {
            this.loadServiceStatus(service);
          }
        }

        if (response.hasRailwayServer) {
          this.loadDatabaseRequirements();
        }
      },
      error: (err) => this.message.set(err?.error?.error?.message ?? 'Could not load services.')
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
        }
      }
    });
  }
}
