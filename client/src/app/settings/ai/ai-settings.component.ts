import { Component } from '@angular/core';
import { AiSetupPreferenceService } from '../../core/services/ai-setup-preference.service';

@Component({
  selector: 'app-ai-settings',
  standalone: true,
  templateUrl: './ai-settings.component.html',
  styleUrl: './ai-settings.component.scss'
})
export class AiSettingsComponent {
  constructor(readonly aiSetup: AiSetupPreferenceService) {}

  setEnabled(value: boolean): void {
    this.aiSetup.setEnabled(value);
  }
}
