import { Component, Input, computed, model } from '@angular/core';
import { APP_LOGOS } from '../../core/constants/app-logos';
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
  readonly selectedLogoKey = model<string | null>(null);
  @Input() compact = false;

  readonly options = computed(() => APP_LOGOS);

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
