import { Component, EventEmitter, Input, Output } from '@angular/core';
import { DeploymentPlan, DeploymentPlanPart } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';

interface BuildConfigRow {
  label: string;
  value: string;
}

@Component({
  selector: 'app-deploy-plan',
  standalone: true,
  imports: [ButtonComponent, IconComponent],
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
    switch (role) {
      case 'website':
        return 'Fast global hosting';
      case 'server':
        return 'Always-on server';
      case 'database':
        return 'Managed database';
      default:
        return '';
    }
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
