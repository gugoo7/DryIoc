import { useState } from 'react';
import { styled } from '@material-ui/core';
import Navbar from './Navbar';
import Sidebar from './Sidebar';
import Routes from '../../Routes';

const DashboardLayoutRoot = styled('div')(
    {
        backgroundColor: '#E9E9E9',
        display: 'flex',
        height: '100%',
        overflow: 'hidden',
        width: '100%'
    }
);

const DashboardLayoutWrapper = styled('div')(
    {
        display: 'flex',
        flex: '1 1 auto',
        overflow: 'hidden',
        paddingTop: 64
    }
);

const DashboardLayoutContainer = styled('div')({
  display: 'flex',
  flex: '1 1 auto',
  overflow: 'hidden'
});

const DashboardLayoutContent = styled('div')({
  flex: '1 1 auto',
  height: '100%',
  overflow: 'auto'
});

const DashboardLayout = () => {
  const [isMobileNavOpen, setMobileNavOpen] = useState(false);

  return (
    <DashboardLayoutRoot>
      <Navbar onMobileNavOpen={() => setMobileNavOpen(true)} />
      <Sidebar
        onMobileClose={() => setMobileNavOpen(false)}
        openMobile={isMobileNavOpen}
      />
      <DashboardLayoutWrapper>
        <DashboardLayoutContainer>
          <DashboardLayoutContent>
            <Routes />
          </DashboardLayoutContent>
        </DashboardLayoutContainer>
      </DashboardLayoutWrapper>
    </DashboardLayoutRoot>
  );
};

export default DashboardLayout;