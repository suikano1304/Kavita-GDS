import {Injectable} from '@angular/core';
import {JumpKey} from '../_models/jumpbar/jump-key';
import {SeriesSortField} from '../_models/metadata/series-filter';
import {SortOptions} from '../_models/metadata/v2/sort-options';
import {Series} from '../_models/series';

const keySize = 22; // Approximate height of compact JumpBar buttons
const maxSampledKeys = 120;
const repeatedLabelMarker = '\u00b7';

@Injectable({
  providedIn: 'root'
})
export class JumpbarService {

  resumeKeys: {[key: string]: string} = {};
  // Used for custom filtered urls
  resumeScroll: {[key: string]: number} = {};


  getResumeKey(key: string) {
    const k = key.toUpperCase();
    if (this.resumeKeys.hasOwnProperty(k)) return this.resumeKeys[k];
    return '';
  }

  getResumePosition(url: string) {
    if (this.resumeScroll.hasOwnProperty(url)) return this.resumeScroll[url];
    return 0;
  }

  saveResumeKey(key: string, value: string) {
    const k = key.toUpperCase();
    this.resumeKeys[k] = value;
  }

  saveResumePosition(url: string, value: number) {
    this.resumeScroll[url] = value;
  }

  generateJumpBar(jumpBarKeys: Array<JumpKey>, currentSize: number) {
    const fullSize = (jumpBarKeys.length * keySize);
    if (currentSize >= fullSize) {
      return this.withRepeatLabels(jumpBarKeys);
    }

    const targetNumberOfKeys = Math.max(2, Math.floor(currentSize / keySize));
    if (jumpBarKeys.length <= targetNumberOfKeys) return this.withRepeatLabels(jumpBarKeys);

    const selectedIndexes = new Set<number>();
    for (let i = 0; i < targetNumberOfKeys; i++) {
      selectedIndexes.add(Math.round(i * (jumpBarKeys.length - 1) / (targetNumberOfKeys - 1)));
    }

    const selectedKeys = Array.from(selectedIndexes)
      .sort((a, b) => a - b)
      .map(index => jumpBarKeys[index]);

    return this.withRepeatLabels(selectedKeys);
  }

  _removeSecondPartOfJumpBar(midPoint: number, numberOfRemovals: number = 1, jumpBarKeys: Array<JumpKey>, jumpBarKeysToRender: Array<JumpKey>) {
    const removedIndexes: Array<number> = [];
    for(let removal = 0; removal < numberOfRemovals; removal++) {
      let min = 100000000;
      let minIndex = -1;
      for(let i = midPoint + 1; i < jumpBarKeys.length - 2; i++) {
        if (jumpBarKeys[i].size < min && !removedIndexes.includes(i)) {
          min = jumpBarKeys[i].size;
          minIndex = i;
        }
      }
      removedIndexes.push(minIndex);
    }
    for(let i = midPoint + 1; i < jumpBarKeys.length - 2; i++) {
      if (!removedIndexes.includes(i)) jumpBarKeysToRender.push(jumpBarKeys[i]);
    }
  }

  _removeFirstPartOfJumpBar(midPoint: number, numberOfRemovals: number = 1, jumpBarKeys: Array<JumpKey>, jumpBarKeysToRender: Array<JumpKey>) {
    const removedIndexes: Array<number> = [];

    for(let removal = 0; removal < numberOfRemovals; removal++) {
      let min = 100000000;
      let minIndex = -1;

      for(let i = 1; i < midPoint; i++) {
        if (jumpBarKeys[i].size < min && !removedIndexes.includes(i)) {
          min = jumpBarKeys[i].size;
          minIndex = i;
        }
      }
      removedIndexes.push(minIndex);
    }

    for(let i = 1; i < midPoint; i++) {
      if (!removedIndexes.includes(i)) jumpBarKeysToRender.push(jumpBarKeys[i]);
    }
  }

  /**
   *
   * @param data An array of objects
   * @param keySelector A method to fetch a string from the object, which is used to classify the JumpKey
   * @returns
   */
   getJumpKeys(data :Array<any>, keySelector: (data: any) => string) {
    const keys: {[key: string]: number} = {};
    data.forEach(obj => {
      try {
        let ch = keySelector(obj).charAt(0).toUpperCase();
        if (!/\p{L}/u.test(ch)) {
          ch = '#';
        }
        if (!keys.hasOwnProperty(ch)) {
          keys[ch] = 0;
        }
        keys[ch] += 1;
      } catch (e) {
        console.error('Failed to calculate jump key for ', obj, e);
      }
    });
    return Object.keys(keys).map(k => {
      k = k.toUpperCase();
      return {
        key: k,
        size: keys[k],
        title: k
      }
    }).sort((a, b) => {
      if (a.key < b.key) return -1;
      if (a.key > b.key) return 1;
      return 0;
    });
  }

  getSeriesJumpKeys(data: Array<Series>, sortOptions?: SortOptions<SeriesSortField>) {
    const sortField = sortOptions?.sortField ?? SeriesSortField.LastModified;
    if (sortField === SeriesSortField.Random) return [];

    switch (sortField) {
      case SeriesSortField.SortName:
        return this.getJumpKeysByKey(data, s => this.getNameKey(s.sortName ?? s.name));
      case SeriesSortField.Created:
        return this.getSampledJumpKeys(data, s => this.getDateKey(s.created));
      case SeriesSortField.LastModified:
        return this.getSampledJumpKeys(data, s => this.getDateKey(s.contentLastModified || s.lastModified || s.created));
      case SeriesSortField.LastChapterAdded:
        return this.getSampledJumpKeys(data, s => this.getDateKey(s.lastChapterAdded));
      case SeriesSortField.TimeToRead:
        return this.getSampledJumpKeys(data, s => this.getReadTimeKey(s.avgHoursToRead));
      case SeriesSortField.ReleaseYear:
        return this.getSampledJumpKeys(data, s => this.getReleaseYearKey(s.releaseYear));
      case SeriesSortField.ReadProgress:
        return this.getSampledJumpKeys(data, s => this.getDateKey(s.latestReadDate));
      case SeriesSortField.UserRating:
        return this.getSampledJumpKeys(data, s => this.getRatingKey(s.userRating, s.hasUserRated));
      case SeriesSortField.UnreadChapterCount:
        return this.getSampledJumpKeys(data, s => this.getUnreadKey(s.pages - s.pagesRead));
      case SeriesSortField.AverageRating:
        return [];
      default:
        return this.getJumpKeysByKey(data, s => this.getNameKey(s.sortName ?? s.name));
    }
  }

  getJumpKeysByKey<T>(data: Array<T>, keySelector: (data: T) => string) {
    const keys = new Map<string, JumpKey>();
    data.forEach((obj, index) => {
      try {
        const key = keySelector(obj);
        if (!keys.has(key)) {
          keys.set(key, {key, title: key, size: 0, index});
        }
        keys.get(key)!.size += 1;
      } catch (e) {
        console.error('Failed to calculate jump key for ', obj, e);
      }
    });

    return Array.from(keys.values());
  }

  private getSampledJumpKeys<T>(data: Array<T>, titleSelector: (data: T) => string) {
    if (data.length === 0) return [];

    const step = Math.max(1, Math.ceil(data.length / maxSampledKeys));
    const keys: Array<JumpKey> = [];

    for (let index = 0; index < data.length; index += step) {
      const title = titleSelector(data[index]);
      keys.push({
        key: `${title}-${index}`,
        title,
        size: Math.min(step, data.length - index),
        index
      });
    }

    return keys;
  }

  private withRepeatLabels(jumpBarKeys: Array<JumpKey>) {
    let previousTitle = '';

    return jumpBarKeys.map(jumpKey => {
      const label = jumpKey.title === previousTitle ? repeatedLabelMarker : jumpKey.title;
      previousTitle = jumpKey.title;
      return {...jumpKey, label};
    });
  }

  private getNameKey(name: string) {
    let ch = (name ?? '').charAt(0).toUpperCase();
    if (!/\p{L}/u.test(ch)) {
      ch = '#';
    }

    return ch;
  }

  private getDateKey(value?: string) {
    const date = this.parseDate(value);
    if (date === null) return 'N/A';

    const year = date.getFullYear();
    const month = `${date.getMonth() + 1}`.padStart(2, '0');
    const day = `${date.getDate()}`.padStart(2, '0');

    if (year === new Date().getFullYear()) return `${month}-${day}`;

    return `${`${year % 100}`.padStart(2, '0')}-${month}`;
  }

  private getReadTimeKey(hours?: number) {
    const value = Number(hours ?? 0);
    if (value <= 0) return 'N/A';
    if (value < 1) return '<1h';
    if (value < 3) return '1-3h';
    if (value < 10) return '3-10h';
    return '10h+';
  }

  private getReleaseYearKey(year?: number) {
    const value = Number(year ?? 0);
    if (!Number.isFinite(value) || value <= 0) return 'N/A';
    return `${Math.floor(value / 10) * 10}s`;
  }

  private getRatingKey(rating?: number, hasRating: boolean = false) {
    if (!hasRating) return 'NR';
    const value = Math.max(1, Math.min(5, Math.ceil(Number(rating ?? 0))));
    return `${value}*`;
  }

  private getUnreadKey(unreadPages: number) {
    if (unreadPages <= 0) return '0';
    if (unreadPages < 100) return '<100p';
    if (unreadPages < 1000) return '<1000p';
    return '1000p+';
  }

  private parseDate(value?: string) {
    if (!value) return null;
    const date = new Date(value);
    if (Number.isNaN(date.getTime()) || date.getFullYear() <= 1) return null;
    return date;
  }
}
