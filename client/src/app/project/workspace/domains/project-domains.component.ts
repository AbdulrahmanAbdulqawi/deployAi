import { Component, computed, inject } from '@angular/core';
import { ProjectWorkspaceContext } from '../project-workspace-context.service';
import { DomainPanelComponent } from '../../domains/domain-panel.component';
import { pickDomainTargetId } from '../../../core/utils/domain-target';

@Component({
  selector: 'app-project-domains',
  standalone: true,
  imports: [DomainPanelComponent],
  templateUrl: './project-domains.component.html',
  styleUrl: './project-domains.component.scss'
})
export class ProjectDomainsComponent {
  readonly context = inject(ProjectWorkspaceContext);

  readonly deployTargetId = computed(() => {
    const project = this.context.project();
    return project ? pickDomainTargetId(project.targets ?? []) : null;
  });
}
