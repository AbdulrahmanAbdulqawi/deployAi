import { Component, EventEmitter, Input, Output } from '@angular/core';
import { DeploymentPlan, DeploymentPlanPart } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';

@Component({
  selector: 'app-deploy-plan',
  standalone: true,
  imports: [ButtonComponent],
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
}
