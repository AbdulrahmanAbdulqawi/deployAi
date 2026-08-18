import { Component } from '@angular/core';
import { ProjectConfigPanelComponent } from './project-config-panel.component';
import { ProjectServicesPanelComponent } from './project-services-panel.component';

@Component({
  selector: 'app-project-settings',
  standalone: true,
  imports: [ProjectConfigPanelComponent, ProjectServicesPanelComponent],
  templateUrl: './project-settings.component.html',
  styleUrl: './project-settings.component.scss'
})
export class ProjectSettingsComponent {}
