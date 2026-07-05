import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

export interface ActivityLine {
  providerName: string;
  sequence: number;
  line: string;
  loggedAt?: string;
}

@Component({
  selector: 'app-live-log-panel',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './live-log-panel.component.html',
  styleUrl: './live-log-panel.component.scss'
})
export class LiveLogPanelComponent {
  @Input() lines: ActivityLine[] = [];
  expanded = false;
}
