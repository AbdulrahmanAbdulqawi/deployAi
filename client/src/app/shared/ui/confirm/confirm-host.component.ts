import { Component } from '@angular/core';
import { ConfirmDialogComponent } from '../../confirm-dialog/confirm-dialog.component';
import { ConfirmService } from './confirm.service';

@Component({
  selector: 'app-ui-confirm-host',
  standalone: true,
  imports: [ConfirmDialogComponent],
  template: `
    @if (confirm.request(); as request) {
      <app-confirm-dialog
        [title]="request.title"
        [message]="request.message"
        [confirmLabel]="request.confirmLabel ?? 'Confirm'"
        [cancelLabel]="request.cancelLabel ?? 'Cancel'"
        [destructive]="request.destructive ?? false"
        (confirm)="confirm.resolve(true)"
        (cancel)="confirm.resolve(false)" />
    }
  `
})
export class ConfirmHostComponent {
  constructor(readonly confirm: ConfirmService) {}
}
