import { Component, Input, computed, inject, model } from '@angular/core';
import { APP_LOGOS } from '../../core/constants/app-logos';
import { ThemeService } from '../../core/services/theme.service';
import { AppLogoComponent } from '../app-logo/app-logo.component';
import { IconComponent } from '../ui/icon/icon.component';

@Component({
  selector: 'app-app-logo-picker',
  standalone: true,
  imports: [AppLogoComponent, IconComponent],
  templateUrl: './app-logo-picker.component.html',
  styleUrl: './app-logo-picker.component.scss',
})
export class AppLogoPickerComponent {
  private readonly theme = inject(ThemeService);

  readonly selectedLogoKey = model<string | null>(null);
  @Input() compact = false;

  readonly options = computed(() => APP_LOGOS);

  readonly themeStyle = computed(() => this.theme.style());

  isSelected(id: string): boolean {
    return this.selectedLogoKey() === id;
  }

  select(id: string): void {
    this.selectedLogoKey.set(this.selectedLogoKey() === id ? null : id);
  }

  clearSelection(): void {
    this.selectedLogoKey.set(null);
  }
}
