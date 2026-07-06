import { Component, Input } from '@angular/core';
import { IconComponent } from '../ui/icon/icon.component';
import { roleLabelForProvider } from '../../core/utils/target-config';

export interface ActivityLine {
  providerName: string;
  sequence: number;
  line: string;
  loggedAt?: string;
}

@Component({
  selector: 'app-live-log-panel',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './live-log-panel.component.html',
  styleUrl: './live-log-panel.component.scss'
})
export class LiveLogPanelComponent {
  @Input() lines: ActivityLine[] = [];
  @Input() connecting = false;
  @Input() readOnly = false;
  expanded = false;

  roleLabel(providerName: string): string {
    return roleLabelForProvider(providerName);
  }
}
