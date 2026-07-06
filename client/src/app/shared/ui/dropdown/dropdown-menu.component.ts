import { Component, ElementRef, EventEmitter, HostListener, Input, Output, signal } from '@angular/core';
import { ButtonVariant } from '../button/button.component';
import { IconComponent, IconName } from '../icon/icon.component';
export interface DropdownMenuItem {
  id: string;
  label: string;
  icon?: IconName;
  destructive?: boolean;
  disabled?: boolean;
  loading?: boolean;
  href?: string;
}

@Component({
  selector: 'app-ui-dropdown',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './dropdown-menu.component.html',
  styleUrl: './dropdown-menu.component.scss'
})
export class DropdownMenuComponent {
  @Input() ariaLabel = 'Actions';
  @Input() triggerVariant: ButtonVariant = 'secondary';  @Input() items: DropdownMenuItem[] = [];
  @Output() select = new EventEmitter<string>();

  readonly open = signal(false);

  constructor(private readonly host: ElementRef<HTMLElement>) {}

  toggle(event: MouseEvent): void {
    event.stopPropagation();
    this.open.update((value) => !value);
  }

  close(): void {
    this.open.set(false);
  }

  onSelect(item: DropdownMenuItem): void {
    if (item.disabled || item.loading || item.href) {
      return;
    }

    this.select.emit(item.id);
    this.close();
  }

  @HostListener('document:click')
  onDocumentClick(): void {
    this.close();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.close();
  }
}
