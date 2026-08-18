import { Component, Input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

export interface TabBarItem {
  label: string;
  path: string;
}

@Component({
  selector: 'app-ui-tab-bar',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './tab-bar.component.html',
  styleUrl: './tab-bar.component.scss'
})
export class TabBarComponent {
  @Input({ required: true }) items: TabBarItem[] = [];
  @Input() ariaLabel = 'Sections';
}
