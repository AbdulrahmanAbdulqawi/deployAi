import { Component, Input } from '@angular/core';

export type IconName =
  | 'check'
  | 'x'
  | 'loader'
  | 'external'
  | 'sun'
  | 'moon'
  | 'alert'
  | 'info'
  | 'plus'
  | 'upload'
  | 'folder'
  | 'clock'
  | 'pencil'
  | 'arrow-left'
  | 'arrow-right'
  | 'trash'
  | 'refresh'
  | 'settings'
  | 'link'
  | 'log'
  | 'more'
  | 'chevron-down'
  | 'rocket'
  | 'code'
  | 'github'
  | 'copy'
  | 'globe'
  | 'search';

export type IconStatusClass = 'success' | 'accent' | 'danger' | 'warning' | 'muted' | '';

@Component({
  selector: 'app-ui-icon',
  standalone: true,
  templateUrl: './icon.component.html',
  styleUrl: './icon.component.scss',
  host: {
    class: 'ui-icon-host',
    '[class.icon-success]': "statusClass === 'success'",
    '[class.icon-accent]': "statusClass === 'accent'",
    '[class.icon-danger]': "statusClass === 'danger'",
    '[class.icon-warning]': "statusClass === 'warning'",
    '[class.icon-muted]': "statusClass === 'muted'"
  }
})
export class IconComponent {
  @Input({ required: true }) name!: IconName;
  @Input() size: 'sm' | 'md' = 'md';
  @Input() statusClass: IconStatusClass = '';

  get isLoader(): boolean {
    return this.name === 'loader';
  }
}
