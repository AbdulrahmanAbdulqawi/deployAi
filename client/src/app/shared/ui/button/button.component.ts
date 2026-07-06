import { Component, Input } from '@angular/core';
import { IconComponent, IconName } from '../icon/icon.component';

export type ButtonVariant = 'primary' | 'secondary' | 'quiet';
export type ButtonTone = 'brand' | 'success';

@Component({
  selector: 'app-ui-button',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './button.component.html',
  styleUrl: './button.component.scss'
})
export class ButtonComponent {
  @Input() variant: ButtonVariant = 'primary';
  @Input() tone: ButtonTone = 'brand';
  @Input() type: 'button' | 'submit' = 'button';
  @Input() disabled = false;
  @Input() block = false;
  @Input() icon?: IconName;
  @Input() loading = false;

  get displayIcon(): IconName | undefined {
    if (this.loading) {
      return 'loader';
    }

    return this.icon;
  }
}
