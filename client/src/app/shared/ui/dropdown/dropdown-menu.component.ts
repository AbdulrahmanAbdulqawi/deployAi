import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectorRef,
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnDestroy,
  Output,
  Renderer2,
  ViewChild,
  inject,
  signal
} from '@angular/core';
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
export class DropdownMenuComponent implements OnDestroy {
  @Input() ariaLabel = 'Actions';
  @Input() triggerVariant: ButtonVariant = 'secondary';
  @Input() items: DropdownMenuItem[] = [];
  @Output() select = new EventEmitter<string>();

  @ViewChild('trigger') trigger?: ElementRef<HTMLButtonElement>;
  @ViewChild('menuPanel') menuPanel?: ElementRef<HTMLElement>;

  readonly open = signal(false);

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly document = inject(DOCUMENT);
  private readonly renderer = inject(Renderer2);
  private readonly cdr = inject(ChangeDetectorRef);
  private portaledMenu: HTMLElement | null = null;
  private removeScrollListener?: () => void;

  toggle(event: MouseEvent): void {
    event.stopPropagation();

    if (this.open()) {
      this.close();
      return;
    }

    this.open.set(true);
    this.cdr.detectChanges();
    this.openMenuPortal();
  }

  close(): void {
    this.unbindScrollListener();

    const menu = this.portaledMenu ?? this.menuPanel?.nativeElement;
    if (menu) {
      this.renderer.addClass(menu, 'dropdown__menu--closed');
      this.renderer.removeClass(menu, 'dropdown__menu--portaled');
      this.renderer.removeStyle(menu, 'top');
      this.renderer.removeStyle(menu, 'left');
    }

    this.portaledMenu = null;
    this.open.set(false);
  }

  onSelect(item: DropdownMenuItem): void {
    if (item.disabled || item.loading || item.href) {
      return;
    }

    this.select.emit(item.id);
    this.close();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as Node;
    if (this.host.nativeElement.contains(target)) {
      return;
    }

    const menu = this.portaledMenu ?? this.menuPanel?.nativeElement;
    if (menu?.contains(target)) {
      return;
    }

    this.close();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.close();
  }

  @HostListener('window:resize')
  onResize(): void {
    if (this.open()) {
      this.updateMenuPosition();
    }
  }

  ngOnDestroy(): void {
    this.unbindScrollListener();
    const menu = this.portaledMenu ?? this.menuPanel?.nativeElement;
    if (menu?.parentNode === this.document.body) {
      this.renderer.removeChild(this.document.body, menu);
    }
  }

  private openMenuPortal(attempt = 0): void {
    const menu = this.menuPanel?.nativeElement;
    if (!menu || !this.open()) {
      return;
    }

    if (!menu.offsetParent && menu.offsetWidth === 0 && attempt < 8) {
      requestAnimationFrame(() => this.openMenuPortal(attempt + 1));
      return;
    }

    this.attachMenuPortal(menu);
  }

  private attachMenuPortal(menu: HTMLElement): void {
    this.renderer.removeClass(menu, 'dropdown__menu--closed');

    if (menu.parentElement !== this.document.body) {
      this.renderer.appendChild(this.document.body, menu);
    }

    this.renderer.addClass(menu, 'dropdown__menu--portaled');
    this.portaledMenu = menu;
    this.bindScrollListener();
    requestAnimationFrame(() => this.updateMenuPosition(menu));
  }

  private bindScrollListener(): void {
    this.unbindScrollListener();
    this.document.addEventListener('scroll', this.onDocumentScroll, true);
    this.removeScrollListener = () => {
      this.document.removeEventListener('scroll', this.onDocumentScroll, true);
    };
  }

  private readonly onDocumentScroll = (): void => {
    if (this.open()) {
      this.updateMenuPosition();
    }
  };

  private unbindScrollListener(): void {
    this.removeScrollListener?.();
    this.removeScrollListener = undefined;
  }

  private updateMenuPosition(menuEl?: HTMLElement): void {
    const trigger = this.trigger?.nativeElement;
    const menu = menuEl ?? this.portaledMenu ?? this.menuPanel?.nativeElement;
    if (!trigger || !menu) {
      return;
    }

    const rect = trigger.getBoundingClientRect();
    const menuWidth = Math.max(menu.offsetWidth, 160);
    const viewportPadding = 8;
    let left = rect.right - menuWidth;
    left = Math.max(viewportPadding, Math.min(left, window.innerWidth - menuWidth - viewportPadding));

    const top = rect.bottom + 8;

    this.renderer.setStyle(menu, 'top', `${top}px`);
    this.renderer.setStyle(menu, 'left', `${left}px`);
  }
}
