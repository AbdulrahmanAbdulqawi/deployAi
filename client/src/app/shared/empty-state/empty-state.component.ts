import { Component, Input } from '@angular/core';

@Component({
  selector: 'app-empty-state',
  standalone: true,
  templateUrl: './empty-state.component.html',
  styleUrl: './empty-state.component.scss'
})
export class EmptyStateComponent {
  @Input({ required: true }) title = '';
  @Input({ required: true }) message = '';
  @Input() actionLabel?: string;
  @Input() actionClick?: () => void;
}
