import { GetIntroPlayLists } from '@lib/smAPI/Custom/CustomCommands';
import { Logger } from '@lib/common/logger';
import { createAsyncThunk } from '@reduxjs/toolkit';


export const fetchGetIntroPlayLists = createAsyncThunk('cache/getGetIntroPlayLists', async (_: void, thunkAPI) => {
  try {
    Logger.debug('Fetching GetIntroPlayLists');
    const fetchDebug = localStorage.getItem('fetchDebug');
    const start = performance.now();
    const response = await GetIntroPlayLists();
    if (fetchDebug) {
      const duration = performance.now() - start;
      Logger.debug(`Fetch GetIntroPlayLists completed in ${duration.toFixed(2)}ms`);
    }

    return {param: _, value: response };
  } catch (error) {
    console.error('Failed to fetch', error);
    return thunkAPI.rejectWithValue({ error: error || 'Unknown error', value: undefined });
  }
});


