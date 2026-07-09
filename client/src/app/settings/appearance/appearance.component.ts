import { Component } from '@angular/core';
import {
  AppearancePreset,
  ThemeService,
} from '../../core/services/theme.service';
import { IconComponent } from '../../shared/ui/icon/icon.component';

interface AppearanceOption {
  id: AppearancePreset;
  label: string;
  description: string;
  style: 'default' | 'notebook';
  mode: 'light' | 'dark';
}

@Component({
  selector: 'app-appearance',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './appearance.component.html',
  styleUrl: './appearance.component.scss',
})
export class AppearanceComponent {
  readonly options: AppearanceOption[] = [
    {
      id: 'default-light',
      label: 'Default Light',
      description: 'Clean surfaces with a light palette.',
      style: 'default',
      mode: 'light',
    },
    {
      id: 'default-dark',
      label: 'Default Dark',
      description: 'Dark surfaces with subtle accent glows.',
      style: 'default',
      mode: 'dark',
    },
    {
      id: 'notebook-light',
      label: 'Notebook Light',
      description: 'Warm ruled paper with modern sans-serif UI.',
      style: 'notebook',
      mode: 'light',
    },
    {
      id: 'notebook-dark',
      label: 'Notebook Dark',
      description: 'Dark stationery with refined indigo accents.',
      style: 'notebook',
      mode: 'dark',
    },
  ];

  constructor(readonly theme: ThemeService) {}

  select(preset: AppearancePreset): void {
    this.theme.setAppearance(preset);
  }

  isSelected(preset: AppearancePreset): boolean {
    return this.theme.appearance() === preset;
  }

  setOrbSnakeEnabled(enabled: boolean): void {
    this.theme.setOrbSnakeEnabled(enabled);
  }

  setOrbDriftersEnabled(enabled: boolean): void {
    this.theme.setOrbDriftersEnabled(enabled);
  }
}
