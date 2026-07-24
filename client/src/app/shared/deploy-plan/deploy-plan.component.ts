import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DeploymentPlan, DeploymentPlanKind, DeploymentPlanPart, ProviderName } from '../../core/models/api.models';
import { isCoolifyProvider } from '../../core/utils/provider-branding';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';

interface BuildConfigRow {
  label: string;
  value: string;
}

@Component({
  selector: 'app-deploy-plan',
  standalone: true,
  imports: [FormsModule, ButtonComponent, IconComponent],
  templateUrl: './deploy-plan.component.html',
  styleUrl: './deploy-plan.component.scss'
})
export class DeployPlanComponent {
  @Input() loading = false;
  @Input() plan: DeploymentPlan | null = null;
  @Input() activeParts: DeploymentPlanPart[] = [];
  @Input() showManualOverride = false;
  @Input() deploying = false;
  @Input() missingConnections: string[] = [];
  /** Projects on the target server. Only surfaced when there is a genuine choice to make. */
  @Input() destinations: { id: string; name: string }[] = [];
  @Input() selectedDestinationId = '';
  /** Only offered for shapes where the domain has to be attached explicitly. */
  @Input() showDomainField = false;
  @Input() customDomain = '';
  /** GitHub Apps configured in Coolify — needed to grant it access to a private repo. */
  @Input() githubApps: { id: string; name: string }[] = [];
  @Input() selectedGithubAppId = '';
  /** True when the target repo is private and Coolify therefore needs a GitHub App picked. */
  @Input() requireGithubApp = false;

  @Output() customDomainChange = new EventEmitter<string>();
  @Output() destinationChange = new EventEmitter<string>();
  @Output() githubAppChange = new EventEmitter<string>();
  @Output() accept = new EventEmitter<void>();
  @Output() override = new EventEmitter<void>();
  @Output() selectOption = new EventEmitter<string>();
  @Output() connectProviders = new EventEmitter<void>();
  @Output() back = new EventEmitter<void>();

  roleLabel(role: string): string {
    switch (role) {
      case 'website':
        return 'Your site';
      case 'server':
        return 'Your API';
      case 'database':
        return 'Your database';
      default:
        return role;
    }
  }

  roleDescription(role: string): string {
    if (this.isSingleOriginComposePlan()) {
      // Under compose these are services inside one deployment, not separately-addressed apps —
      // calling the site a "Static site on Coolify" implies a second URL that doesn't exist.
      switch (role) {
        case 'website':
          return 'Web service — serves the site and proxies /api';
        case 'server':
          return 'API service — reached through the site, not its own domain';
        case 'database':
          return 'PostgreSQL on Coolify';
        default:
          return '';
      }
    }

    switch (role) {
      case 'website':
        if (!this.isCoolifyFullStackPlan()) {
          return 'Fast global hosting';
        }
        // A Next.js/Nuxt/etc. app runs a Node server on Coolify, not a static bundle.
        return this.isServerRenderedWebsite() ? 'Web app on Coolify' : 'Static site on Coolify';
      case 'server':
        return this.isCoolifyFullStackPlan() ? 'API on Coolify' : 'Always-on server';
      case 'database':
        return this.isCoolifyFullStackPlan() ? 'PostgreSQL on Coolify' : 'Managed database';
      default:
        return '';
    }
  }

  private isServerRenderedWebsite(): boolean {
    const website = this.plan?.parts.find(part => part.role === 'website');
    const framework = website?.framework?.trim().toLowerCase();
    return framework === 'next' || framework === 'nextjs' || framework === 'nuxt'
      || framework === 'sveltekit' || framework === 'remix';
  }

  isSingleOriginComposePlan(): boolean {
    return this.plan?.planKind === DeploymentPlanKind.CoolifyCompose;
  }

  isCoolifyFullStackPlan(): boolean {
    return this.plan?.planKind === DeploymentPlanKind.CoolifyFullStack ||
      this.isSingleOriginComposePlan() ||
      (this.activeParts.some(part => part.role === 'website' && isCoolifyProvider(part.providerName)) &&
        this.activeParts.some(part => part.role === 'server' && isCoolifyProvider(part.providerName)));
  }

  destinationProviderLabel(part: DeploymentPlanPart): string {
    if (isCoolifyProvider(part.providerName)) {
      return 'Coolify';
    }
    if (part.providerName === ProviderName.Vercel) {
      return 'Vercel';
    }
    if (part.providerName === ProviderName.Railway) {
      return 'Railway';
    }
    return part.providerName;
  }

  planTitle(): string {
    if (this.isSingleOriginComposePlan()) {
      return 'Single-origin Coolify setup';
    }
    return this.isCoolifyFullStackPlan() ? 'Coolify full-stack setup' : 'AI Recommended Setup';
  }

  planSubtitle(): string {
    if (this.isSingleOriginComposePlan()) {
      return 'One Docker Compose deployment on your Coolify server, served from a single domain';
    }
    return this.isCoolifyFullStackPlan()
      ? 'Website, API, and database on your Coolify server'
      : 'Based on source code analysis';
  }

  websitePart(): DeploymentPlanPart | undefined {
    return this.activeParts.find(part => part.role === 'website');
  }

  serverPart(): DeploymentPlanPart | undefined {
    return this.activeParts.find(part => part.role === 'server');
  }

  detectedFramework(): string {
    const website = this.websitePart();
    const server = this.serverPart();

    if (website?.framework && server?.framework && website.framework !== server.framework) {
      return `${this.formatFramework(website.framework)} + ${this.formatFramework(server.framework)}`;
    }

    const framework = website?.framework ?? server?.framework;
    return framework ? this.formatFramework(framework) : 'Detected from repository';
  }

  runtimeEnvironment(): string {
    const server = this.serverPart();
    if (server?.dockerfilePath) {
      return 'Docker container';
    }

    if (server?.framework) {
      return `${this.formatFramework(server.framework)} runtime`;
    }

    return 'Node.js 20.x';
  }

  buildConfigRows(): BuildConfigRow[] {
    const part = this.websitePart() ?? this.serverPart();
    if (!part) {
      return [];
    }

    const rows: BuildConfigRow[] = [];
    if (part.buildCommand) {
      rows.push({ label: 'Build Command:', value: part.buildCommand });
    }
    if (part.startCommand) {
      rows.push({ label: 'Start Command:', value: part.startCommand });
    }
    if (part.outputDirectory) {
      rows.push({ label: 'Output Dir:', value: part.outputDirectory });
    }
    if (part.installCommand) {
      rows.push({ label: 'Install Cmd:', value: part.installCommand });
    }

    return rows;
  }

  private formatFramework(framework: string): string {
    const normalized = framework.toLowerCase();
    switch (normalized) {
      case 'nextjs':
      case 'next.js':
        return 'Next.js (React)';
      case 'react':
        return 'React';
      case 'angular':
        return 'Angular';
      case 'vue':
        return 'Vue.js';
      case 'nuxt':
        return 'Nuxt.js';
      case 'dotnet':
      case '.net':
        return '.NET';
      default:
        return framework;
    }
  }
}
