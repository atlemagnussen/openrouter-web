import Layout from '../components/Layout';
import Link from 'next/link';
import dataService from '../services/dataservice';
import formatters from '../services/formatters';

const Index = props => (
  <Layout>
    <h1>Active Clients</h1>
      {props.leases.map(lease => (
          <p key={lease.id}>
          <Link href="/device/[id]" as={`/device/${lease.mac}`}>
            <a>{lease.ip} - {lease.host} - {formatters.toSmallDateTime(lease.end)}</a>
          </Link>
          </p>
      ))}
  </Layout>
);

Index.getInitialProps = async function() {
    const data = await dataService.getActiveLeases();
    console.log(`Show data fetched. Count: ${data.length}`);

    return {
        leases: data
    };
};

export default Index;
