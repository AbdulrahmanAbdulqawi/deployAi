import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { GitHubBranch, FrontendBuildProfile, ServerBuildProfile, ProjectDetail, ProjectTarget } from '../core/models/api.models';
import { parseTargetConfig, providerLabel } from '../core/utils/target-config';
import { RepoFolderPickerComponent } from '../shared/repo-folder-picker/repo-folder-picker.component';
import { ConfirmService } from '../shared/ui/confirm/confirm.service';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { IconComponent } from '../shared/ui/icon/icon.component';

type TargetConfig = ReturnType<typeof parseTargetConfig> & ProjectTarget;

@Component({
  selector: 'app-project-edit',
  standalone: true,
  imports: [FormsModule, RepoFolderPickerComponent, ButtonComponent, IconComponent],
  templateUrl: './project-edit.component.html',
  styleUrl: './project-edit.component.scss'
})
export class ProjectEditComponent implements OnInit {
  readonly project = signal<ProjectDetail | null>(null);
  readonly branches = signal<GitHubBranch[]>([]);
  readonly editableTargets = signal<TargetConfig[]>([]);
  readonly pageLoading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly saving = signal(false);
  readonly deleting = signal(false);
  readonly message = signal<string | null>(null);
  readonly isError = signal(false);
  readonly detectingProvider = signal<string | null>(null);
  readonly autoDetectingServer = signal(false);

  private static readonly FrontendDirectoryNames = ['client', 'web', 'frontend', 'app'];

  name = '';
  defaultBranch = '';
  private projectId = '';

  constructor(
    private readonly api: ApiService,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly confirm: ConfirmService
  ) {}

  ngOnInit(): void {
    this.projectId = this.route.snapshot.paramMap.get('id') ?? '';
    this.pageLoading.set(true);
    this.loadError.set(null);

    this.api.getProject(this.projectId).subscribe({
      next: (project) => {
        this.project.set(project);
        this.name = project.name;
        this.defaultBranch = project.defaultBranch;
        this.editableTargets.set(
          project.targets
            .filter(target => parseTargetConfig(target.config).role !== 'database')
            .map(target => ({
              ...target,
              ...parseTargetConfig(target.config)
            }))
        );
        for (const target of project.targets) {
          const parsed = parseTargetConfig(target.config);
          if (parsed.role === 'database') {
            continue;
          }
          if (target.providerName === 'vercel' && parsed.rootDirectory && !parsed.outputDirectory) {
            this.detectBuildProfile(parsed.rootDirectory, target.providerName, false);
          }
          if (target.providerName === 'railway' && (parsed.serviceDirectory ?? parsed.rootDirectory)) {
            this.detectBuildProfile(parsed.serviceDirectory ?? parsed.rootDirectory ?? '', target.providerName, false);
          }
        }
        this.loadBranches(project.githubRepoFullName);
        this.pageLoading.set(false);
      },
      error: (err) => {
        this.loadError.set(err?.error?.error?.message ?? 'Could not load that app.');
        this.pageLoading.set(false);
      }
    });
  }

  goToProject(): void {
    void this.router.navigate(['/projects', this.projectId]);
  }

  goToProjects(): void {
    void this.router.navigate(['/dashboard']);
  }

  repoOwner(): string | null {
    return this.project()?.githubRepoFullName.split('/')[0] ?? null;
  }

  repoName(): string | null {
    return this.project()?.githubRepoFullName.split('/')[1] ?? null;
  }

  targetLabel(target: TargetConfig): string {
    if (target.role === 'website') return 'Website folder';
    if (target.role === 'server') return 'Server folder';
    return 'Folder';
  }

  targetSectionTitle(target: TargetConfig): string {
    if (target.providerName === 'vercel') return 'Website';
    if (target.providerName === 'railway') return 'API';
    return this.targetLabel(target);
  }

  targetSectionSubtitle(target: TargetConfig): string {
    if (target.providerName === 'vercel') return 'Vercel Deployment';
    if (target.providerName === 'railway') return 'Railway Infrastructure';
    return providerLabel(target.providerName);
  }

  hasTargetPath(target: TargetConfig): boolean {
    const path = target.serviceDirectory ?? target.rootDirectory;
    return path !== undefined && path !== null;
  }

  targetFolderPath(target: TargetConfig): string | null {
    const path = target.serviceDirectory ?? target.rootDirectory;
    return path !== undefined && path !== null ? path : null;
  }

  buildSummaryRows(): {
    component: string;
    framework?: string;
    buildCommand?: string;
    startCommand?: string;
  }[] {
    return this.editableTargets()
      .filter(target => target.providerName === 'vercel' || target.providerName === 'railway')
      .map(target => ({
        component: this.targetSectionTitle(target),
        framework: target.framework,
        buildCommand: target.buildCommand,
        startCommand: target.startCommand
      }))
      .filter(row => row.framework || row.buildCommand || row.startCommand);
  }

  providerLabel = providerLabel;

  hasRailwayTarget(): boolean {
    return this.editableTargets().some(target => target.providerName === 'railway');
  }

  onFolderSelected(providerName: string, path: string): void {
    this.editableTargets.update(targets =>
      targets.map(t =>
        t.providerName === providerName
          ? { ...t, rootDirectory: path, serviceDirectory: path }
          : t
      )
    );

    if (providerName === 'vercel' || providerName === 'railway') {
      this.detectBuildProfile(path, providerName, false);
    }
  }

  redetectBuildSettings(target: TargetConfig): void {
    if (target.providerName !== 'vercel' && target.providerName !== 'railway') {
      return;
    }

    this.detectBuildProfile(target.rootDirectory ?? '', target.providerName, true);
  }

  autoDetectServerFolder(): void {
    const owner = this.repoOwner();
    const repo = this.repoName();
    if (!owner || !repo || !this.defaultBranch) {
      return;
    }

    this.autoDetectingServer.set(true);
    this.api.detectServerBuildProfile(owner, repo, '', this.defaultBranch).subscribe({
      next: (profile) => {
        const folder = profile.serviceDirectory ?? profile.rootDirectory;
        if (!folder && !profile.framework) {
          this.message.set('No server folder found in this repo.');
          this.isError.set(true);
          return;
        }

        this.editableTargets.update(targets =>
          targets.map(t =>
            t.providerName === 'railway' ? this.applyServerProfile(t, profile) : t
          )
        );
        this.message.set(
          folder
            ? `Detected server folder: ${folder}${profile.framework ? ` (${profile.framework})` : ''}.`
            : 'Build settings updated.'
        );
        this.isError.set(false);
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not detect server folder.');
        this.isError.set(true);
      },
      complete: () => {
        this.autoDetectingServer.set(false);
      }
    });
  }

  isMisconfiguredRailwayTarget(target: TargetConfig): boolean {
    if (target.providerName !== 'railway') {
      return false;
    }

    const folder = (target.serviceDirectory ?? target.rootDirectory ?? '').toLowerCase();
    const folderName = folder.split('/').pop() ?? folder;
    const isFrontendFolder = ProjectEditComponent.FrontendDirectoryNames.includes(folderName);

    if (target.framework === 'node' && isFrontendFolder) {
      return true;
    }

    return target.framework === 'node' &&
      target.buildCommand === 'npm run build' &&
      !target.startCommand;
  }

  private detectBuildProfile(path: string, providerName: string, showSuccessMessage: boolean): void {
    const owner = this.repoOwner();
    const repo = this.repoName();
    if (!owner || !repo || !this.defaultBranch) {
      return;
    }

    this.detectingProvider.set(providerName);
    if (providerName === 'railway') {
      this.api.detectServerBuildProfile(owner, repo, path, this.defaultBranch).subscribe({
        next: (profile) => {
          this.editableTargets.update(targets =>
            targets.map(t =>
              t.providerName === providerName ? this.applyServerProfile(t, profile) : t
            )
          );
          if (showSuccessMessage) {
            this.message.set('Build settings updated.');
            this.isError.set(false);
          }
        },
        error: (err) => {
          this.message.set(err?.error?.error?.message ?? 'Could not detect build settings.');
          this.isError.set(true);
        },
        complete: () => {
          this.detectingProvider.set(null);
        }
      });
      return;
    }

    this.api.detectBuildProfile(owner, repo, path, this.defaultBranch).subscribe({
      next: (profile) => {
        this.editableTargets.update(targets =>
          targets.map(t =>
            t.providerName === providerName ? this.applyFrontendProfile(t, profile) : t
          )
        );
        if (showSuccessMessage) {
          this.message.set('Build settings updated.');
          this.isError.set(false);
        }
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not detect build settings.');
        this.isError.set(true);
      },
      complete: () => {
        this.detectingProvider.set(null);
      }
    });
  }

  private applyFrontendProfile(
    target: TargetConfig,
    profile: FrontendBuildProfile
  ): TargetConfig {
    return {
      ...target,
      rootDirectory: profile.rootDirectory,
      outputDirectory: profile.outputDirectory,
      buildCommand: profile.buildCommand,
      installCommand: profile.installCommand,
      framework: profile.framework
    };
  }

  private applyServerProfile(
    target: TargetConfig,
    profile: ServerBuildProfile
  ): TargetConfig {
    return {
      ...target,
      rootDirectory: profile.rootDirectory,
      buildCommand: profile.buildCommand ?? undefined,
      installCommand: profile.installCommand ?? undefined,
      startCommand: profile.startCommand ?? undefined,
      framework: profile.framework ?? undefined,
      dockerfilePath: profile.dockerfilePath ?? undefined,
      serviceDirectory: profile.serviceDirectory ?? profile.rootDirectory
    };
  }

  save(): void {
    const project = this.project();
    if (!project) return;

    this.saving.set(true);
    this.message.set(null);

    this.api.updateProject(this.projectId, {
      name: this.name,
      defaultBranch: this.defaultBranch,
      targets: this.editableTargets().map(target => ({
        providerName: target.providerName,
        credentialId: target.credentialId,
        providerProjectId: target.providerProjectId,
        config: this.buildTargetConfig(target)
      }))
    }).subscribe({
      next: () => void this.router.navigate(['/projects', this.projectId]),
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not save those changes.');
        this.isError.set(true);
        this.saving.set(false);
      }
    });
  }

  cancel(): void {
    void this.router.navigate(['/projects', this.projectId]);
  }

  async remove(): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: 'Delete app?',
      message: 'This permanently removes the app, all deployment history, and hosting on Railway and Vercel. This cannot be undone.',
      confirmLabel: 'Delete',
      destructive: true
    });
    if (!confirmed) {
      return;
    }

    this.deleting.set(true);
    this.api.deleteProject(this.projectId).subscribe({
      next: () => void this.router.navigate(['/dashboard']),
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not remove that app.');
        this.isError.set(true);
        this.deleting.set(false);
      }
    });
  }

  private loadBranches(repoFullName: string): void {
    const [owner, repo] = repoFullName.split('/');
    if (!owner || !repo) return;

    this.api.listBranches(owner, repo).subscribe({
      next: (response) => this.branches.set(response.branches),
      error: () => this.branches.set([{ name: this.defaultBranch }])
    });
  }

  private buildTargetConfig(target: TargetConfig): string {
    const config: Record<string, string> = {
      rootDirectory: target.rootDirectory ?? '',
      role: target.role ?? (target.providerName === 'vercel' ? 'website' : 'server')
    };

    if (target.serviceDirectory) {
      config['serviceDirectory'] = target.serviceDirectory;
    }

    if (target.outputDirectory) {
      config['outputDirectory'] = target.outputDirectory;
    }
    if (target.buildCommand) {
      config['buildCommand'] = target.buildCommand;
    }
    if (target.installCommand) {
      config['installCommand'] = target.installCommand;
    }
    if (target.startCommand) {
      config['startCommand'] = target.startCommand;
    }
    if (target.framework) {
      config['framework'] = target.framework;
    }
    if (target.dockerfilePath) {
      config['dockerfilePath'] = target.dockerfilePath;
    }
    if (target.serviceDirectory) {
      config['serviceDirectory'] = target.serviceDirectory;
    }

    return JSON.stringify(config);
  }
}
