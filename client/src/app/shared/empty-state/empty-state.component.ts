import { Component, Input } from '@angular/core';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [ButtonComponent, IconComponent],
  templateUrl: './empty-state.component.html',
  styleUrl: './empty-state.component.scss'
})
export class EmptyStateComponent {
  @Input({ required: true }) title = '';
  @Input() message = '';
  @Input() actionLabel?: string;
  @Input() actionClick?: () => void;
}
